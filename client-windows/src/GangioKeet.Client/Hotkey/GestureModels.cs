namespace GangioKeet.Client.Hotkey;

public enum GestureKind
{
    Start,
    StopAndTranscribe,
    HandsFreeEntered,

    /// <summary>Annulla la dettatura senza trascrivere (tap accidentale su Caps Lock).</summary>
    Abort,
}
