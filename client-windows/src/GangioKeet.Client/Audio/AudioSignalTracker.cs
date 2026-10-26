namespace GangioKeet.Client.Audio;

/// <summary>
/// Diagnosi pure dell'audio consegnato: distingue nessun callback (zero campioni) da microfono muto
/// (campioni tutti a zero) da voce debole (picco vicino a zero ma non nullo). Non avviene mai una
/// scartatura automatica dei dati: <see cref="AppendPcm16"/> conta campioni, calcola il picco e
/// restituisce solo il livello informativo della chiamata; classificare o scartare resta al chiamante.
/// </summary>
public sealed class AudioSignalTracker
{
    private const int BytesPerSample = 2;
    private const float FullScale = 32768f;

    private long _sampleCount;
    private int _peak;
    private bool _allZero = true;
    private byte _pendingByte;
    private bool _hasPendingByte;

    /// <summary>
    /// Appende campioni PCM16 little endian (anche spezzati tra chiamate, senza perdere il mezzo
    /// campione) e restituisce il livello del picco di QUESTA chiamata, normalizzato [0..1].
    /// </summary>
    public double AppendPcm16(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return 0;
        }

        int chunkPeak = 0;
        int chunkSamples = 0;
        int index = 0;

        if (_hasPendingByte)
        {
            chunkPeak = AbsSample(_pendingByte, pcm[0]);
            chunkSamples = 1;
            index = 1;
        }

        for (; index + BytesPerSample <= pcm.Length; index += BytesPerSample)
        {
            chunkPeak = Math.Max(chunkPeak, AbsSample(pcm[index], pcm[index + 1]));
            chunkSamples++;
        }

        if (chunkPeak > 0)
        {
            _allZero = false;
            _peak = Math.Max(_peak, chunkPeak);
        }

        _sampleCount += chunkSamples;

        byte leftover = index < pcm.Length ? pcm[pcm.Length - 1] : byte.MinValue;
        _hasPendingByte = index < pcm.Length;
        _pendingByte = leftover;

        return Math.Min(chunkPeak / FullScale, 1.0);
    }

    public AudioSummary BuildSummary()
    {
        double peak = _sampleCount == 0 ? 0 : Math.Min(_peak / FullScale, 1.0);
        bool allZero = _sampleCount > 0 && _allZero;
        return new AudioSummary(_sampleCount, allZero, peak);
    }

    public void Reset()
    {
        _sampleCount = 0;
        _peak = 0;
        _allZero = true;
        _pendingByte = 0;
        _hasPendingByte = false;
    }

    private static int AbsSample(byte low, byte high)
    {
        short sample = (short)(low | (high << 8));
        return Math.Abs((int)sample);
    }
}
