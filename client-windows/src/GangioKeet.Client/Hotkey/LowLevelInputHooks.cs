using System.Threading.Channels;

namespace GangioKeet.Client.Hotkey;

/// <summary>
/// Orchestrazione degli hook globali: il callback del backend esegue solo decode, cattura
/// dell'handle, decisione di soppressione e accodamento non bloccante (coda bounded 128). Un
/// dispatcher dedicato consegna <see cref="RawEvent"/>, così risoluzione exe/titolo, UIPI,
/// microfono e rete restano fuori dal callback. L'overflow produce un errore esplicito e azzera
/// lo stato della policy, invece di perdere eventi in silenzio.
/// </summary>
public sealed class LowLevelInputHooks : IInputHooks
{
    public const int DefaultCapacity = 128;

    private readonly IInputHookBackend _backend;
    private readonly HookSuppressionPolicy _policy;
    private readonly Channel<RawKeyEvent> _channel;
    private readonly CancellationTokenSource _dispatchCts = new();
    private readonly int _capacity;
    private Thread? _hookThread;
    private Thread? _dispatchThread;
    private int _started;
    private int _disposed;

    public LowLevelInputHooks(IInputHookBackend backend, HookSuppressionPolicy policy, int capacity = DefaultCapacity)
    {
        _backend = backend;
        _policy = policy;
        _capacity = capacity;
        _channel = Channel.CreateBounded<RawKeyEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public event EventHandler<RawKeyEvent>? RawEvent;

    /// <summary>
    /// Errore esplicito (installazione fallita o coda input in overflow). L'installazione avviene
    /// sul thread degli hook e l'esito arriva qui in modo asincrono: sottoscrivere prima di
    /// <see cref="Start"/> per non perdere il primo errore.
    /// </summary>
    public event EventHandler<string>? Failed;

    public bool AbortActive
    {
        set => _policy.AbortActive = value;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Input hooks are already started.");
        }

        _dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "GangioKeet input dispatch",
        };
        _dispatchThread.Start();
        _hookThread = new Thread(RunHookThread)
        {
            IsBackground = true,
            Name = "GangioKeet input hooks",
        };
        _hookThread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backend.Stop();
        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _backend.Dispose();

        _dispatchCts.Cancel();
        _channel.Writer.TryComplete();
        bool dispatchStopped = _dispatchThread?.Join(TimeSpan.FromSeconds(1)) ?? true;
        if (dispatchStopped)
        {
            _dispatchCts.Dispose();
        }
    }

    private void RunHookThread()
    {
        try
        {
            _backend.Run(_policy.InstallsMouseHook, HandleInput);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, $"Input hooks could not be installed: {ex.Message}");
        }
    }

    private bool HandleInput(RawInput raw)
    {
        HookDecision decision = _policy.Decide(raw.VirtualKey, raw.IsDown, raw.FromMouse);
        if (decision.Emit)
        {
            RawKeyEvent item = new(raw.VirtualKey, raw.IsDown, raw.FromMouse, raw.ForegroundHandle);
            if (!_channel.Writer.TryWrite(item))
            {
                _policy.Reset();
                // Il callback dell'hook non deve mai bloccare: gli errori escono dal pool.
                RaiseFailure($"Input queue overflow ({_capacity} events): state reset and events dropped.");
            }
        }

        return decision.Suppress;
    }

    private void RaiseFailure(string message)
    {
        ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(this, message));
    }

    private void DispatchLoop()
    {
        // Thread dedicato: un handler lento non sottrae un thread del pool alla sessione o allo
        // shutdown dell'host di test.
        DispatchAsync(_dispatchCts.Token).GetAwaiter().GetResult();
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        try
        {
            await foreach (RawKeyEvent item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    RawEvent?.Invoke(this, item);
                }
                catch (Exception ex)
                {
                    // Un subscriber che lancia non deve abbattere il processo né fermare i hook.
                    Failed?.Invoke(this, $"Input handler failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
