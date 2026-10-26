using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Client REST OpenAI-compatible: <c>POST {base}/v1/audio/transcriptions?format=pcm</c> con
/// multipart <c>file=dictation.pcm</c> (application/octet-stream) e <c>profile</c>; <c>model</c>
/// viene omesso. Possiede una sola richiesta per invocazione: due deadline distinte — upload e
/// risposta, quest'ultima iscritta solo al completamento della scrittura del body — governate da
/// timer del <see cref="TimeProvider"/> iniettato; la risposta viene letta con
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> e il corpo è limitato a 1 MiB.
/// </summary>
/// <remarks>
/// Il HttpClient condiviso deve avere <c>Timeout = Timeout.InfiniteTimeSpan</c>: le uniche
/// deadline valide sono uploadTimeout e responseTimeout, mai un timeout globale HttpClient in
/// conflitto. Nessun retry interno e nessuna decisione di fallback (policy esterna).
/// </remarks>
public sealed class RestDictationClient
{
    /// <summary>Limite di lettura del corpo della risposta: 1 MiB.</summary>
    public const int MaxResponseBytes = 1_048_576;

    private const string TranscriptionsRelativePath = "v1/audio/transcriptions?format=pcm";

    private const string FilePartContentType = "application/octet-stream";

    private const int ErrorMessageTruncateLimit = 300;

    private readonly HttpClient _http;

    private readonly TimeProvider _time;

    public RestDictationClient(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public async Task<DictationOutcome> TranscribeAsync(
        Uri baseUri,
        ReadOnlyMemory<byte> pcm,
        string profile,
        string? token,
        TimeSpan uploadTimeout,
        TimeSpan responseTimeout,
        CancellationToken ct)
    {
        using RequestPhase phase = new(ct, responseTimeout);
        using HttpRequestMessage request = BuildRequest(baseUri, pcm, profile, token, phase, _time);

        HttpResponseMessage? response = null;
        try
        {
            phase.StartUploadDeadline(_time, uploadTimeout);

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, phase.Cts.Token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;

            // Quando gli header arrivano, la fase di upload è comunque conclusa (o è stata chiusa
            // dal ciclo della risposta): la deadline di upload non deve più interferire con la
            // lettura del corpo.
            phase.CompleteWrite(_time);

            string body = await ReadBodyWithinLimitAsync(response.Content, phase.Cts.Token).ConfigureAwait(false);
            return status is >= 200 and <= 299
                ? ParseResultOutcome(body)
                : StatusErrorOutcome(status, body);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                return new DictationOutcome(
                    DictationStatus.Error,
                    null,
                    new DictationError("aborted", "REST transcription aborted by the caller.", Retryable: false));
            }

            bool uploadDone = Volatile.Read(ref phase.UploadCompleted) != 0;
            return new DictationOutcome(
                DictationStatus.Error,
                null,
                uploadDone
                    ? new DictationError(
                        "timeout",
                        $"The REST response did not complete within the {Seconds(responseTimeout)} response deadline.",
                        Retryable: true)
                    : new DictationError(
                        "timeout",
                        $"The REST upload did not complete within the {Seconds(uploadTimeout)} upload deadline.",
                        Retryable: true));
        }
        catch (HttpRequestException ex)
        {
            return new DictationOutcome(
                DictationStatus.Error,
                null,
                new DictationError("connect_failed", $"REST request failed: {ex.Message}", Retryable: true));
        }
        catch (ProtocolFrameException ex)
        {
            return new DictationOutcome(
                DictationStatus.Error,
                null,
                new DictationError("protocol", ex.Message, Retryable: false));
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static HttpRequestMessage BuildRequest(Uri baseUri, ReadOnlyMemory<byte> pcm, string profile, string? token, RequestPhase phase, TimeProvider time)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(baseUri, TranscriptionsRelativePath))
        {
            Content = new MultipartPcmContent(pcm, profile, Guid.NewGuid().ToString("N"), phase, time),
        };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<string> ReadBodyWithinLimitAsync(HttpContent content, CancellationToken token)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream buffer = new();
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaxResponseBytes)
                {
                    throw new ProtocolFrameException(
                        $"The REST response body exceeds the 1 MiB limit ({MaxResponseBytes} bytes).");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#");

    private static DictationOutcome ParseResultOutcome(string body)
    {
        try
        {
            return new DictationOutcome(DictationStatus.Result, ParseResultJson(body), null);
        }
        catch (ProtocolFrameException ex)
        {
            return new DictationOutcome(
                DictationStatus.Error,
                null,
                new DictationError("protocol", ex.Message, Retryable: false));
        }
    }

    private static ResultMessage ParseResultJson(string json)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProtocolFrameException($"The REST result body is not valid JSON. BODY=[{json}]");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolFrameException("The REST result body is not a JSON object.");
        }

        string? finalText = GetOptionalString(root, "final_text");
        string? text = GetOptionalString(root, "text");
        if (finalText is null && text is null)
        {
            throw new ProtocolFrameException("The REST result JSON is missing its 'text' member.");
        }

        string rawText = GetOptionalString(root, "raw_text") ?? finalText ?? text!;
        bool fastPath = TryGetBool(root, "fast_path") ?? false;
        DictationMetrics metrics = ParseMetrics(root);
        string[] warnings = root.TryGetProperty("warnings", out JsonElement warningsElement)
            && warningsElement.ValueKind == JsonValueKind.Array
            ? warningsElement.EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : item.GetRawText())
                .ToArray()
            : [];

        return new ResultMessage(rawText, finalText ?? text!, fastPath, metrics, warnings);
    }

    private static DictationMetrics ParseMetrics(JsonElement root)
    {
        if (root.TryGetProperty("metrics", out JsonElement metricsElement) && metricsElement.ValueKind == JsonValueKind.Object)
        {
            double sttMs = TryGetDouble(metricsElement, "stt_ms") ?? 0;
            double llmMs = TryGetDouble(metricsElement, "llm_ms") ?? 0;
            double totalMs = TryGetDouble(metricsElement, "total_ms") ?? 0;
            return new DictationMetrics(sttMs, llmMs, totalMs);
        }

        return new DictationMetrics(0, 0, 0);
    }

    private static DictationOutcome StatusErrorOutcome(int status, string body)
    {
        string message = BuildStatusErrorMessage(body, out string? serverCode);
        string code = serverCode ?? StatusToCode(status);
        return new DictationOutcome(
            DictationStatus.Error,
            null,
            new DictationError(code, message, Retryable: status >= 500, HttpStatus: status));
    }

    private static string StatusToCode(int status)
    {
        return status switch
        {
            401 => "unauthorized",
            403 => "forbidden",
            404 => "not_found",
            413 => "payload_too_large",
            426 => "upgrade_required",
            503 => "busy",
            _ => "unexpected_response",
        };
    }

    private static string BuildStatusErrorMessage(string body, out string? serverCode)
    {
        serverCode = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return $"REST transcription failed with the body: {Truncate(body)}";
            }

            serverCode = GetOptionalString(root, "code");
            string? message = GetOptionalString(root, "message");
            string? detail = GetOptionalString(root, "detail");

            if (detail is null && root.TryGetProperty("detail", out JsonElement detailElement)
                && detailElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                // FastAPI può serializzare il detail come lista di errori: teniamo solo i msg.
                detail = detailElement.ValueKind == JsonValueKind.Array
                    ? string.Join("; ", detailElement.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("msg", out JsonElement msgElement)
                            ? msgElement.GetString()
                            : item.GetRawText())
                        .Where(static item => !string.IsNullOrEmpty(item)))
                    : detailElement.GetRawText();
            }

            return $"REST transcription failed: {detail ?? message ?? Truncate(body)}";
        }
        catch (JsonException)
        {
            return $"REST transcription failed with the body: {Truncate(body)}";
        }
    }

    private static string Truncate(string body)
    {
        body = body.Trim();
        return body.Length <= ErrorMessageTruncateLimit ? body : body[..ErrorMessageTruncateLimit] + "…";
    }

    private static string? GetOptionalString(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool? TryGetBool(JsonElement parent, string property)
    {
        if (parent.TryGetProperty(property, out JsonElement element))
        {
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null;
        }

        return null;
    }

    private static double? TryGetDouble(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out JsonElement element) && element.TryGetDouble(out double value)
            ? value
            : null;
    }

    /// <summary>
    /// Stato per-invocazione della richiesta: due deadline distinte sulla stessa CTS collegata al
    /// token del chiamante, gestite con timer del TimeProvider. La deadline di upload copre
    /// l'invio del body; al completamento della scrittura la sostituisce la deadline di risposta
    /// (20 s di configurazione): un upload che resta a piede non erode mai la deadline di risposta.
    /// </summary>
    private sealed class RequestPhase : IDisposable
    {
        private readonly TimeSpan _responseTimeout;

        private ITimer? _uploadTimer;

        private ITimer? _responseTimer;

        private int _disposed;

        public RequestPhase(CancellationToken userCancelled, TimeSpan responseTimeout)
        {
            Cts = CancellationTokenSource.CreateLinkedTokenSource(userCancelled);
            _responseTimeout = responseTimeout;
        }

        public readonly CancellationTokenSource Cts;

        /// <summary>1 quando la fase di scrittura è conclusa: da lì in poi vale la deadline di risposta.</summary>
        public int UploadCompleted;

        public void StartUploadDeadline(TimeProvider time, TimeSpan uploadTimeout)
        {
            _uploadTimer = CreateCancellingTimer(time, uploadTimeout, isUploadDeadline: true);
        }

        /// <summary>
        /// Fine della fase di scrittura, segnalata dal content a WriteAsync conclusa (idempotente):
        /// disarma la deadline di upload e arma la deadline di risposta.
        /// </summary>
        public void CompleteWrite(TimeProvider time)
        {
            if (Interlocked.Exchange(ref UploadCompleted, 1) != 0)
            {
                return;
            }

            ITimer? uploadTimer = Interlocked.Exchange(ref _uploadTimer, null);
            uploadTimer?.Dispose();
            _responseTimer = CreateCancellingTimer(time, _responseTimeout, isUploadDeadline: false);
        }

        private ITimer CreateCancellingTimer(TimeProvider time, TimeSpan dueTime, bool isUploadDeadline)
        {
            return time.CreateTimer(
                _ => CancelFromDeadline(isUploadDeadline),
                null,
                dueTime,
                Timeout.InfiniteTimeSpan);
        }

        private void CancelFromDeadline(bool isUploadDeadline)
        {
            // Un callback già in coda può eseguire dopo Dispose (che non attende i callback già
            // schedulati): con la CTS eliminata Cancel lancia ObjectDisposedException sul thread del
            // timer. Un callback di upload deschedulato oltre CompleteWrite inoltre non deve
            // cancellare una richiesta già passata alla fase di risposta.
            if (Volatile.Read(ref _disposed) != 0
                || (isUploadDeadline && Volatile.Read(ref UploadCompleted) != 0))
            {
                return;
            }

            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Dispose è passato tra la lettura del flag e Cancel: non c'è più nulla da cancellare.
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
            ITimer? uploadTimer = Interlocked.Exchange(ref _uploadTimer, null);
            uploadTimer?.Dispose();
            ITimer? responseTimer = Interlocked.Exchange(ref _responseTimer, null);
            responseTimer?.Dispose();
            Cts.Dispose();
        }
    }

    /// <summary>
    /// Multipart form-data scritto a mano (file=dictation.pcm octet-stream + profile): la lunghezza
    /// resta sconosciuta (Transfer-Encoding: chunked) così la serializzazione scrive direttamente
    /// sulla connessione e la backpressure di un server che non legge si riflette a valle fino a
    /// <see cref="SerializeToStreamAsync"/>; al termine della scrittura segnaliamo la fine della
    /// fase di upload, così la deadline di risposta parte solo a scrittura conclusa.
    /// </summary>
    private sealed class MultipartPcmContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _pcm;

        private readonly byte[] _profileBytes;

        private readonly byte[] _filePartHead;

        private readonly byte[] _profilePartHead;

        private readonly byte[] _finalBoundary;

        private readonly RequestPhase _phase;

        private readonly TimeProvider _time;

        public MultipartPcmContent(ReadOnlyMemory<byte> pcm, string profile, string boundary, RequestPhase phase, TimeProvider time)
        {
            _pcm = pcm;
            _phase = phase;
            _time = time;
            _profileBytes = Encoding.UTF8.GetBytes(profile);
            _filePartHead = Encoding.UTF8.GetBytes(
                $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"dictation.pcm\"\r\nContent-Type: {FilePartContentType}\r\n\r\n");
            _profilePartHead = Encoding.UTF8.GetBytes(
                $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"profile\"\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n");
            _finalBoundary = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

            Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
            Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? transportContext)
        {
            // WriteAsync contro la connessione reale: un server che non legge il body mette la
            // scrittura in backpressure e la fase di upload non completa mai entro la sua deadline.
            await stream.WriteAsync(_filePartHead).ConfigureAwait(false);
            await stream.WriteAsync(_pcm).ConfigureAwait(false);
            await stream.WriteAsync(_profilePartHead).ConfigureAwait(false);
            await stream.WriteAsync(_profileBytes).ConfigureAwait(false);
            await stream.WriteAsync(_finalBoundary).ConfigureAwait(false);

            _phase.CompleteWrite(_time);
        }

        protected override bool TryComputeLength(out long length)
        {
            // Streaming esplicito (chunked): con Content-Length noto il pipeline avrebbe
            // pre-serializzato tutto il body in memoria, e la "fine della scrittura" non
            // corrisponderebbe più agli ultimi byte già passati al socket.
            length = 0;
            return false;
        }
    }
}
