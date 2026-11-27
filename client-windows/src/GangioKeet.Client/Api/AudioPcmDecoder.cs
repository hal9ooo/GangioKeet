namespace GangioKeet.Client.Api;

using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

/// <summary>
/// Audio ricevuto che non si riesce a decodificare nel contratto della pipeline: formato
/// sconosciuto, contenitore corrotto o codec che Media Foundation non sa aprire.
/// Deriva da <see cref="Exception"/> come le altre eccezioni del progetto: <c>InvalidDataException</c>
/// previsto dal piano è <c>sealed</c> e non è ereditabile.
/// </summary>
public sealed class UnsupportedAudioFormatException : Exception
{
    public UnsupportedAudioFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Audio in arrivo oltre il tetto che il decodificatore accetta in memoria. Tipo distinto da
/// <see cref="UnsupportedAudioFormatException"/> perché il server HTTP lo mappa a <c>413</c> e non
/// a <c>415</c>: il contenuto può essere perfettamente valido, è la dimensione a essere troppo
/// grande. Il limite applicativo resta quello del corpo HTTP; questo è solo la rete di sicurezza
/// per un upload chunked che arriva senza <c>Content-Length</c>.
/// </summary>
public sealed class AudioTooLargeException : Exception
{
    public AudioTooLargeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Porta un file audio qualsiasi al contratto della pipeline: PCM16 little endian, mono, 16 kHz.
/// Misurato su questa macchina con Media Foundation: WAV, MP3, M4A, AAC, WebM/Opus e FLAC passano;
/// OGG/Vorbis no, perché il decoder Vorbis non è installato e <see cref="MediaFoundationReader"/>
/// solleva <see cref="MediaFoundationException"/>. Il limite è documentato e fissato da un test.
/// <see cref="ToPcm16Mono16k"/> legge il flusso in memoria entro <see cref="DefaultMaxInputBytes"/>,
/// lo copia in un file temporaneo (Media Foundation vuole un percorso) e lo cancella sempre, anche
/// quando la decodifica fallisce. Un payload senza campioni è rifiutato come formato non supportato:
/// il motore ASR non accetta PCM vuoto e il server ne farebbe un 500.
/// </summary>
public static class AudioPcmDecoder
{
    /// <summary>
    /// Massimo audio accettato in memoria: 26 MB, appena sopra i 25 MB del corpo HTTP.
    /// Non deve mai scattare per una richiesta legittima; serve contro un upload chunked
    /// senza <c>Content-Length</c>, che altrimenti verrebbe bufferizzato fino all'esaurimento.
    /// </summary>
    public const int DefaultMaxInputBytes = 26 * 1024 * 1024;

    /// <summary>Ampiezza del blocco <c>WAVE</c> in testa al formato prodotto nelle prove.</summary>
    private const int WaveHeaderBytes = 44;

    /// <summary>Dimensione della lettura a blocchi mentre si bufferizza l'input.</summary>
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>Formato del contratto: PCM16 mono 16 kHz. Nuovo a ogni uso: <see cref="WaveFormat"/> è mutabile.</summary>
    private static WaveFormat PipelineFormat => new(16000, 16, 1);

    /// <summary>
    /// Decodifica <paramref name="audio"/> e restituisce solo il payload PCM (senza header WAV).
    /// <paramref name="formatName"/> descrive la conversione, per log e diagnosi.
    /// </summary>
    /// <exception cref="UnsupportedAudioFormatException">Il contenuto non è un audio decodificabile o è vuoto.</exception>
    /// <exception cref="AudioTooLargeException">L'input supera <see cref="DefaultMaxInputBytes"/>.</exception>
    public static byte[] ToPcm16Mono16k(Stream audio, out string formatName)
        => ToPcm16Mono16k(audio, DefaultMaxInputBytes, out formatName);

    /// <summary>
    /// Come <see cref="ToPcm16Mono16k(Stream, out string)"/>, con il tetto di buffer esplicito:
    /// i test lo abbassano per non allocare 26 MB.
    /// </summary>
    internal static byte[] ToPcm16Mono16k(Stream audio, int maxInputBytes, out string formatName)
    {
        ArgumentNullException.ThrowIfNull(audio);

        using MemoryStream source = new();
        if (audio.CanSeek)
        {
            audio.Position = 0;
        }

        CopyWithLimit(audio, source, maxInputBytes);
        if (source.Length == 0)
        {
            throw new UnsupportedAudioFormatException("Audio vuoto.");
        }

        source.Position = 0;

        // Già nel contratto: nessun resampling, si toglie solo l'header WAV.
        if (TryReadPipelineWav(source, out byte[] straight))
        {
            if (straight.Length == 0)
            {
                // WAV legale ma senza campioni: il motore ASR lo rifiuterebbe con
                // ArgumentException, cioè un 500. Meglio un formato non supportato.
                throw new UnsupportedAudioFormatException("Audio vuoto o senza campioni.");
            }

            formatName = "16000Hz/1ch -> 16000Hz/1ch";
            return straight;
        }

        source.Position = 0;
        string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".audio");
        try
        {
            using (FileStream file = File.Create(tempPath))
            {
                source.CopyTo(file);
            }

            using MediaFoundationReader reader = OpenReader(tempPath);
            formatName = $"{reader.WaveFormat.SampleRate}Hz/{reader.WaveFormat.Channels}ch -> 16000Hz/1ch";
            byte[] pcm = ResampleToPipeline(reader);
            if (pcm.Length == 0)
            {
                throw new UnsupportedAudioFormatException("Audio vuoto o senza campioni.");
            }

            return pcm;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Copia l'input a blocchi e si ferma appena il totale supera <paramref name="maxInputBytes"/>,
    /// così un flusso senza <c>Content-Length</c> non viene bufferizzato fino all'esaurimento.
    /// </summary>
    private static void CopyWithLimit(Stream audio, MemoryStream target, int maxInputBytes)
    {
        byte[] buffer = new byte[ReadChunkBytes];
        long total = 0;
        int read;
        while ((read = audio.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxInputBytes)
            {
                throw new AudioTooLargeException(
                    $"Audio troppo grande: il limite è {maxInputBytes / (1024 * 1024.0):0.#} MB.");
            }

            target.Write(buffer, 0, read);
        }
    }

    /// <summary>Apre il file con Media Foundation traducendo ogni errore di formato noto.</summary>
    private static MediaFoundationReader OpenReader(string path)
    {
        try
        {
            return new MediaFoundationReader(path);
        }
        catch (Exception error) when (IsFormatError(error))
        {
            throw new UnsupportedAudioFormatException(
                "Formato audio non supportato: usa WAV, MP3, M4A, FLAC o WebM.", error);
        }
    }

    /// <summary>Passa dal resampler di Media Foundation e recide l'header WAV dal risultato.</summary>
    private static byte[] ResampleToPipeline(MediaFoundationReader reader)
    {
        try
        {
            using MediaFoundationResampler resampler = new(reader, PipelineFormat);

            using MemoryStream wav = new();
            WaveFileWriter.WriteWavFileToStream(wav, resampler);
            byte[] bytes = wav.ToArray();
            int header = FindDataOffset(bytes);
            return bytes.AsSpan(header).ToArray();
        }
        catch (Exception error) when (IsFormatError(error))
        {
            throw new UnsupportedAudioFormatException(
                "Formato audio non supportato: usa WAV, MP3, M4A, FLAC o WebM.", error);
        }
    }

    /// <summary>Media Foundation segnala i formati che non sa aprire in tre modi diversi.</summary>
    private static bool IsFormatError(Exception error) => error is not OutOfMemoryException
        && (error is InvalidOperationException
            or COMException
            or MediaFoundationException
            or NotSupportedException
            or ArgumentException
            or FormatException);

    /// <summary>
    /// Scorciatoia per l'audio che è già PCM16 mono 16 kHz: restituisce il payload dopo l'header,
    /// così l'input non passa per un resampler che lo lascerebbe intatto ma più lento.
    /// </summary>
    private static bool TryReadPipelineWav(Stream wav, out byte[] pcm)
    {
        pcm = [];
        try
        {
            using WaveFileReader reader = new(wav);
            WaveFormat format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm
                || format.SampleRate != PipelineFormat.SampleRate
                || format.Channels != PipelineFormat.Channels
                || format.BitsPerSample != PipelineFormat.BitsPerSample)
            {
                return false;
            }

            // Riscrittura canonica: un header WAV standard, poi il payload dopo i 44 byte.
            using MemoryStream canonical = new();
            WaveFileWriter.WriteWavFileToStream(canonical, reader);
            byte[] bytes = canonical.ToArray();
            pcm = bytes.AsSpan(FindDataOffset(bytes)).ToArray();
            return true;
        }
        catch (Exception error) when (IsFormatError(error) || error is IOException)
        {
            // Non è un WAV leggibile (o è troncato): decide Media Foundation, che sa dire no.
            return false;
        }
    }

    /// <summary>Offset del primo blocco <c>data</c>, oltre il quale comincia il PCM.</summary>
    private static int FindDataOffset(byte[] wav)
    {
        int offset = 12; // "RIFF" + dimensione + "WAVE"
        while (offset + 8 <= wav.Length)
        {
            int size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0)
            {
                break;
            }

            if (wav.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                return offset + 8;
            }

            offset += 8 + size + (size & 1);
        }

        return Math.Min(WaveHeaderBytes, wav.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: un file temporaneo non deve mascherare la decodifica.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}