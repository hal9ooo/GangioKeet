namespace GangioKeet.Client.Audio;

/// <summary>
/// Normalizza il livello del microfono sulla base dell'ultima registrazione.
/// La taratura riguarda solo la visualizzazione della pill: il PCM inviato
/// all'ASR resta invariato, quindi nessun rischio di clipping o perdita di
/// qualità nella trascrizione.
/// </summary>
public sealed class MicrophoneLevelCalibration
{
    /// <summary>Picco di riferimento usato finché non esiste una taratura.</summary>
    public const double DefaultReference = 0.30;

    /// <summary>Sotto questo valore il microfono è considerato troppo debole
    /// per essere amplificato ulteriormente (eviterebbe di amplificare il rumore).</summary>
    public const double MinReference = 0.04;

    /// <summary>Un microfono molto forte non deve mai schiacciare la wave.</summary>
    public const double MaxReference = 0.75;

    private const double MinObservedPeak = 0.03;
    private const double NewMeasurementWeight = 0.7;

    private readonly object _gate = new();
    private readonly MicrophoneCalibrationStore? _store;
    private double _reference;

    public MicrophoneLevelCalibration(
        double reference = DefaultReference,
        MicrophoneCalibrationStore? store = null)
    {
        _reference = Clamp(reference);
        _store = store;
    }

    /// <summary>Picco di riferimento corrente (0..1).</summary>
    public double Reference
    {
        get
        {
            lock (_gate)
            {
                return _reference;
            }
        }
    }

    /// <summary>Riporta il picco grezzo del blocco sulla scala tarata.</summary>
    public double Normalize(double level)
    {
        double reference;
        lock (_gate)
        {
            reference = _reference;
        }

        if (!double.IsFinite(level) || level <= 0)
        {
            return 0;
        }

        return Math.Clamp(level / reference, 0, 1);
    }

    /// <summary>
    /// Aggiorna la taratura con il 90° percentile dell'ultima registrazione.
    /// Il peso alto sulla nuova misura fa sì che la dettatura successiva sia
    /// già calibrata; una singola registrazione anomala non la rovina del tutto.
    /// </summary>
    public void Observe(double speechPeak)
    {
        if (!double.IsFinite(speechPeak) || speechPeak < MinObservedPeak)
        {
            return;
        }

        double updated;
        lock (_gate)
        {
            double target = Clamp(speechPeak);
            _reference = Clamp(
                _reference * (1 - NewMeasurementWeight) + target * NewMeasurementWeight);
            updated = _reference;
        }

        _store?.Save(updated);
    }

    private static double Clamp(double value)
    {
        if (!double.IsFinite(value))
        {
            return DefaultReference;
        }

        return Math.Clamp(value, MinReference, MaxReference);
    }
}
