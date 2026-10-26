using System.IO;
using System.Media;
using GangioKeet.Client.Controller;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Riproduce suoni sintetizzati tramite <see cref="SoundPlayer"/> (WAV in memoria).
/// Tono Start: 880 Hz / 60 ms. Stop: 660 Hz / 60 ms. Error: 220 Hz / 180 ms.
/// Fade-out 10 ms. Disattivabile impostando <see cref="Enabled"/> = false.
/// </summary>
public sealed class SynthSoundPlayer : ISoundPlayer
{
    private static readonly int SampleRate = 22050;

    private static readonly byte[] StartWav = GenerateToneWav(880, 60);
    private static readonly byte[] StopWav = GenerateToneWav(660, 60);
    private static readonly byte[] ErrorWav = GenerateToneWav(220, 180);

    private readonly SoundPlayer _startPlayer;
    private readonly SoundPlayer _stopPlayer;
    private readonly SoundPlayer _errorPlayer;

    public bool Enabled { get; set; } = true;

    public SynthSoundPlayer()
    {
        _startPlayer = new SoundPlayer(new MemoryStream(StartWav));
        _stopPlayer = new SoundPlayer(new MemoryStream(StopWav));
        _errorPlayer = new SoundPlayer(new MemoryStream(ErrorWav));

        _startPlayer.Load();
        _stopPlayer.Load();
        _errorPlayer.Load();
    }

    public void Play(SoundKind kind)
    {
        if (!Enabled) return;

        switch (kind)
        {
            case SoundKind.Start:
                _startPlayer.Play();
                break;
            case SoundKind.Stop:
                _stopPlayer.Play();
                break;
            case SoundKind.Error:
                _errorPlayer.Play();
                break;
        }
    }

    public void Dispose()
    {
        _startPlayer.Dispose();
        _stopPlayer.Dispose();
        _errorPlayer.Dispose();
    }

    /// <summary>
    /// Genera un file WAV PCM16 mono con un tono puro alla frequenza indicata.
    /// Include fade-out di 10 ms per evitare click.
    /// </summary>
    public static byte[] GenerateToneWav(int frequencyHz, int durationMs)
    {
        int totalSamples = SampleRate * durationMs / 1000;
        int fadeSamples = SampleRate * 10 / 1000; // 10 ms fade
        short[] samples = new short[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / SampleRate;
            double amplitude = 1.0;

            // Fade-out sugli ultimi fadeSamples campioni
            int remaining = totalSamples - i;
            if (remaining <= fadeSamples)
            {
                amplitude = (double)remaining / fadeSamples;
            }

            double value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * t);
            samples[i] = (short)(value * short.MaxValue);
        }

        return EncodeWav(samples, SampleRate);
    }

    private static byte[] EncodeWav(short[] samples, int sampleRate)
    {
        int dataSize = samples.Length * 2; // 16-bit
        int fileSize = 44 + dataSize;

        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);

        // RIFF header
        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write(fileSize - 8);
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt chunk
        w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16); // chunk size
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2); // byte rate
        w.Write((short)2); // block align
        w.Write((short)16); // bits per sample

        // data chunk
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write(dataSize);

        foreach (short s in samples)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }
}
