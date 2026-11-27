using GangioKeet.Client.Api;
using NAudio.Wave;
using Xunit;

namespace GangioKeet.Client.Tests.Api;

public sealed class AudioPcmDecoderTests
{
    private static byte[] Wav(int sampleRate, int channels, double seconds, int seed = 1)
    {
        using MemoryStream stream = new();
        WaveFormat format = new(sampleRate, 16, channels);
        using (WaveFileWriter writer = new(stream, format))
        {
            byte[] buffer = new byte[(int)(format.AverageBytesPerSecond * seconds)];
            new Random(seed).NextBytes(buffer);
            writer.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
    }

    [Fact]
    public void ResamplesStereoToMono16k()
    {
        byte[] pcm = AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(Wav(44100, 2, 2)), out string format);
        Assert.Contains("44100", format);
        double seconds = pcm.Length / 32000.0;
        Assert.InRange(seconds, 1.9, 2.1);
    }

    [Fact]
    public void AcceptsAudioAlreadyAt16k()
    {
        byte[] source = Wav(16000, 1, 1);
        byte[] pcm = AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(source), out _);
        Assert.Equal(source.Length - 44, pcm.Length);
    }

    [Fact]
    public void RejectsBytesThatAreNotAudio()
    {
        Assert.Throws<UnsupportedAudioFormatException>(() =>
            AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream("not audio at all"u8.ToArray()), out _));
    }

    /// <summary>WAV valido, 16 kHz mono PCM16, ma con il blocco <c>data</c> vuoto.</summary>
    private static byte[] EmptyWav()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(0);
        }

        return stream.ToArray();
    }

    [Fact]
    public void RejectsEmptyAudioPayload()
    {
        // WAV legale ma senza campioni: il motore ASR rifiuta il PCM vuoto con
        // ArgumentException, che il server mapperebbe a 500. Il decoder deve
        // fermarlo prima, come formato non supportato.
        byte[] empty = EmptyWav();
        Assert.Equal(44, empty.Length);
        Assert.Throws<UnsupportedAudioFormatException>(() =>
            AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(empty), out _));
    }

    [Fact]
    public void RejectsBytesThatAreNotDecodableAudio()
    {
        // Questi byte non sono audio decodificabile per Media Foundation, qualunque codec
        // sia installato: il test pinna il messaggio documentato per un contenuto
        // indecifrabile, non il limite OGG/Vorbis. Quel limite è stato verificato a mano
        // con un OGG vero (docs/api-server.md, «Verifica locale»): un fixture sintetico non
        // potrebbe distinguere «codec assente» da «fixture non valido».
        byte[] notAudio = "OggS"u8.ToArray().Concat(new byte[2048]).ToArray();
        UnsupportedAudioFormatException error = Assert.Throws<UnsupportedAudioFormatException>(() =>
            AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(notAudio), out _));
        Assert.Contains("WAV", error.Message);
    }

    [Fact]
    public void RejectsInputAboveTheBufferLimit()
    {
        // Un upload chunked senza Content-Length arriverebbe qui senza limiti:
        // il decodificatore si ferma da solo. Limite abbassato per non allocare 26 MB.
        byte[] tooBig = new byte[4096];
        Assert.Throws<AudioTooLargeException>(() =>
            AudioPcmDecoder.ToPcm16Mono16k(
                new MemoryStream(tooBig), maxInputBytes: 1024, out _));
    }

    [Fact]
    public void AcceptsInputExactlyAtTheBufferLimit()
    {
        byte[] atLimit = Wav(16000, 1, 0.02);
        byte[] pcm = AudioPcmDecoder.ToPcm16Mono16k(
            new MemoryStream(atLimit), maxInputBytes: atLimit.Length, out _);
        Assert.Equal(atLimit.Length - 44, pcm.Length);
    }

    [Fact]
    public void TheDefaultBufferLimitSitsAboveTheBodyLimit()
    {
        // 25 MB è il limite del corpo HTTP previsto dal piano: quello del
        // decodificatore deve stare appena sopra e non scattare mai per primo.
        Assert.Equal(26 * 1024 * 1024, AudioPcmDecoder.DefaultMaxInputBytes);
    }
}
