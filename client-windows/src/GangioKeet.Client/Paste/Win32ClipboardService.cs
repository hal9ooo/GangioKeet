using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GangioKeet.Client.Paste;

/// <summary>
/// Dispatcher STA dedicato: apre la clipboard solo sul thread che possiede l'apartment STA, così
/// le API Win32 non dipendono dal thread chiamante e non bloccano il loop del controller.
/// </summary>
public sealed class StaPasteDispatcher : IPasteDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public StaPasteDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "GangioKeet clipboard STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(StaPasteDispatcher));
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, ct);

        return completion.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (Action action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }
}

/// <summary>
/// Clipboard Win32: backup di testo e DIB, scrittura con sequence number, ripristino solo se la
/// clipboard è ancora quella pubblicata (un copia esterno prevale). Apertura contesa: massimo
/// 5 tentativi a 20 ms.
/// </summary>
public sealed class Win32ClipboardService : IClipboardService
{
    private const uint CfText = 1;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const int MaxOpenAttempts = 5;
    private const int OpenRetryDelayMs = 20;

    public ClipboardSnapshot Backup()
    {
        if (!OpenClipboardWithRetry())
        {
            throw new ClipboardUnavailableException("The clipboard could not be opened for backup.");
        }

        try
        {
            string? text = ReadText();
            byte[]? dib = ReadDib();
            return new ClipboardSnapshot(text, dib);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public uint SetText(string text)
    {
        if (!OpenClipboardWithRetry())
        {
            throw new ClipboardUnavailableException("The clipboard could not be opened for publishing.");
        }

        try
        {
            EmptyClipboard();
            SetTextHandle(text);
        }
        finally
        {
            CloseClipboard();
        }

        // Windows incrementa il sequence number alla chiusura della clipboard dopo una
        // modifica: leggerlo qui (dopo CloseClipboard) è l'unico valore che TryRestore
        // potrà osservare. Leggerlo prima del close restituisce il valore precedente e
        // fa fallire ogni ripristino.
        return GetClipboardSequenceNumber();
    }

    public bool TryRestore(ClipboardSnapshot snapshot, uint publishedSequence)
    {
        if (!OpenClipboardWithRetry())
        {
            return false;
        }

        try
        {
            if (GetClipboardSequenceNumber() != publishedSequence)
            {
                return false;
            }

            EmptyClipboard();
            if (snapshot.Text is not null)
            {
                SetTextHandle(snapshot.Text);
            }

            if (snapshot.Dib is not null)
            {
                SetDibHandle(snapshot.Dib);
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string? ReadText()
    {
        IntPtr handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadDib()
    {
        IntPtr handle = GetClipboardData(CfDib);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int size = (int)GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            byte[] buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, size);
            return buffer;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static void SetTextHandle(string text)
    {
        IntPtr memory = Marshal.StringToHGlobalUni(text);
        SetClipboardData(CfUnicodeText, memory);
    }

    private static void SetDibHandle(byte[] dib)
    {
        IntPtr memory = Marshal.AllocHGlobal(dib.Length);
        Marshal.Copy(dib, 0, memory, dib.Length);
        SetClipboardData(CfDib, memory);
    }

    private static bool OpenClipboardWithRetry()
    {
        for (int attempt = 0; attempt < MaxOpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}

public sealed class ClipboardUnavailableException : Exception
{
    public ClipboardUnavailableException(string message)
        : base(message)
    {
    }
}
