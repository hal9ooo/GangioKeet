namespace GangioKeet.Client.Ui;

/// <summary>
/// Storia breve del livello microfono per la forma d'onda della pill: 20 barre,
/// quantizzate in quattro bande, con attacco immediato e rilascio graduale.
/// Dopo otto campioni consecutivi di silenzio (400 ms a 50 ms) la riga è piatta.
/// Non conserva audio: solo il livello già calcolato dalla cattura.
/// </summary>
public sealed class WaveformHistory
{
    public const int BarCount = 20;

    private const double SilenceThreshold = 0.02;
    private const int SilenceSamples = 8;
    private const double ReleaseFactor = 0.85;

    private readonly Queue<double> _displayed = new();
    private double _last;
    private int _silentSamples;

    public IReadOnlyList<byte> Levels
    {
        get
        {
            var levels = new List<byte>(_displayed.Count);
            foreach (double value in _displayed)
            {
                levels.Add(Quantize(value));
            }

            return levels;
        }
    }

    public bool IsSilent => _silentSamples >= SilenceSamples;

    public void Push(double level)
    {
        level = Math.Clamp(level, 0, 1);

        if (level < SilenceThreshold)
        {
            _silentSamples++;
            if (_silentSamples >= SilenceSamples)
            {
                _displayed.Clear();
                _last = 0;
                return;
            }
        }
        else
        {
            _silentSamples = 0;
        }

        double displayed = Math.Max(level, _last * ReleaseFactor);
        _last = displayed;
        _displayed.Enqueue(displayed);

        while (_displayed.Count > BarCount)
        {
            _displayed.Dequeue();
        }
    }

    /// <summary>Azzera la storia all'inizio di una nuova dettatura.</summary>
    public void Reset()
    {
        _displayed.Clear();
        _last = 0;
        _silentSamples = 0;
    }

    private static byte Quantize(double level) => level switch
    {
        < 0.02 => 0,
        < 0.15 => 1,
        < 0.45 => 2,
        _ => 3,
    };
}
