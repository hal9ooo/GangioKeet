namespace GangioKeet.Client.Audio;

using NAudio.Wave;

/// <summary>
/// Frammenta il flusso PCM16 in chunk di dimensione fissa (~100 ms = 3200 byte a 16 kHz mono).
/// <see cref="Feed"/> emette solo chunk interi e conserva il resto; <see cref="Flush"/> evacua il
/// resto alla chiusura della sessione: l'ultimo chunk può essere più corto ed è sempre allineato a
/// 2 byte (un resto dispari completa con un byte zero, dato che il flusso PCM16 legittimo arriva a
/// campioni interi). Nessun byte viene perso tra feed e flush.
/// </summary>
public sealed class AudioChunker(int targetChunkBytes = AudioChunker.TargetChunkBytes)
{
    /// <summary>3200 byte = 100 ms di audio 16 kHz mono PCM16.</summary>
    public const int TargetChunkBytes = 3200;

    private const int BytesPerSample = 2;

    private byte[] _buffer = new byte[Math.Max(targetChunkBytes * 2, 64)];
    private int _count;

    /// <summary>Accoda i byte e restituisce i chunk interi pronti (copia indipendente dal buffer interno).</summary>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> pcm)
    {
        List<byte[]>? chunks = null;
        if (pcm.Length > 0)
        {
            EnsureCapacity(_count + pcm.Length);
            pcm.CopyTo(_buffer.AsSpan(_count));
            _count += pcm.Length;
            chunks = [];
        }

        int offset = 0;
        while (_count - offset >= targetChunkBytes)
        {
            byte[] chunk = new byte[targetChunkBytes];
            _buffer.AsSpan(offset, targetChunkBytes).CopyTo(chunk);
            chunks ??= [];
            chunks.Add(chunk);
            offset += targetChunkBytes;
        }

        if (offset > 0)
        {
            _buffer.AsSpan(offset, _count - offset).CopyTo(_buffer);
            _count -= offset;
        }

        return chunks ?? (IReadOnlyList<byte[]>)[];
    }

    /// <summary>Evacua il resto della sessione: corto, allineato a 2 byte, vuoto se non c'è nulla.</summary>
    public byte[] Flush()
    {
        int length = _count;
        if (length % 2 != 0)
        {
            // Resto di mezzo campione: completato con zero per conservare l'allineamento PCM16.
            length++;
        }

        byte[] tail = new byte[length];
        _buffer.AsSpan(0, _count).CopyTo(tail);
        _count = 0;
        return tail;
    }

    public void Reset()
    {
        _count = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        int next = Math.Max(_buffer.Length * 2, required);
        byte[] grown = new byte[next];
        _buffer.AsSpan(0, _count).CopyTo(grown);
        _buffer = grown;
    }
}

/// <summary>Conversione float [-1..1] in PCM16 signed little endian, con saturazione fuori scala.</summary>
public static class Pcm16
{
    private const float Scale = 32768f;

    public static byte[] FromFloat(ReadOnlySpan<float> samples)
    {
        byte[] pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            // Round evita la deriva di truncation, Clamp gestisce la saturazione del nuovo campione.
            int value = (int)MathF.Round(samples[i] * Scale);
            value = Math.Clamp(value, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)value;
            pcm[(i * 2) + 1] = (byte)(value >> 8);
        }

        return pcm;
    }

    /// <summary>
    /// Converte un buffer nativo WASAPI (spesso <c>WAVE_FORMAT_EXTENSIBLE</c>, non
    /// <c>WaveFormatEncoding.IeeeFloat</c>) in PCM16 mono: downmix dei canali e conversione
    /// float→PCM16. Riconosce il formato reale dal subformat, non solo dall'encoding.
    /// </summary>
    public static byte[] FromCaptureBuffer(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        int channels = Math.Max(format.Channels, 1);
        int bytesPerSample = format.BitsPerSample / 8;

        if (IsIeeeFloat(format) && bytesPerSample == 4)
        {
            int frames = buffer.Length / (4 * channels);
            float[] mono = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(
                        buffer.Slice(((frame * channels) + channel) * 4, 4));
                }

                mono[frame] = sum / channels;
            }

            return FromFloat(mono);
        }

        if (IsPcm16(format) && bytesPerSample == 2)
        {
            int frames = buffer.Length / (2 * channels);
            byte[] mono = new byte[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                int sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToInt16(
                        buffer.Slice(((frame * channels) + channel) * 2, 2));
                }

                short value = (short)(sum / channels);
                mono[frame * 2] = (byte)value;
                mono[(frame * 2) + 1] = (byte)(value >> 8);
            }

            return mono;
        }

        throw new NotSupportedException(
            $"Unsupported capture format: {format.Encoding}, {format.BitsPerSample} bit, {format.Channels} channel(s).");
    }

    private static bool IsIeeeFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return true;
        }

        return format.Encoding == WaveFormatEncoding.Extensible
            && format.BitsPerSample == 32
            && format is WaveFormatExtensible extensible
            && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
    }

    private static bool IsPcm16(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            return true;
        }

        return format.Encoding == WaveFormatEncoding.Extensible
            && format.BitsPerSample == 16
            && format is WaveFormatExtensible extensible
            && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
    }
}
