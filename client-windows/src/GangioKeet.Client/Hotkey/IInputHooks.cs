namespace GangioKeet.Client.Hotkey;

/// <summary>Evento grezzo già filtrato: il controller riceve solo hotkey, mouse configurato ed Esc consumato.</summary>
public sealed record RawKeyEvent(int VirtualKey, bool IsDown, bool FromMouse, nint ForegroundHandle);

/// <summary>
/// Hook globali: <see cref="Start"/> avvia il message loop dedicato; <see cref="AbortActive"/>
/// abilita la soppressione di Esc durante Connecting/Recording/Processing. Il backend decide la
/// soppressione nel callback, prima di qualunque lavoro non banale.
/// </summary>
public interface IInputHooks : IDisposable
{
    event EventHandler<RawKeyEvent>? RawEvent;

    /// <summary>Errore esplicito (installazione fallita o coda input in overflow).</summary>
    event EventHandler<string>? Failed;

    bool AbortActive { set; }

    void Start();
}

/// <summary>Payload del callback: l'handle è catturato sul thread dell'hook, prima dell'evento.</summary>
public readonly record struct RawInput(int VirtualKey, bool IsDown, bool FromMouse, nint ForegroundHandle);

/// <summary>
/// Backend sostituibile del message loop: <see cref="Run"/> installa, pompa e disinstalla sul
/// proprio thread; <see cref="Stop"/> lo sblocca postando WM_QUIT. Il callback restituisce true
/// quando l'evento va soppresso.
/// </summary>
public interface IInputHookBackend : IDisposable
{
    void Run(bool installMouseHook, Func<RawInput, bool> onInput);

    void Stop();
}
