using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class RestDictationClientTests
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

    internal const string TranscriptionsRelativePath = "v1/audio/transcriptions";

    private const string DetailResultJson =
        """{"text":"Finale.","raw_text":"trascrizione grezza","fast_path":true,"metrics":{"stt_ms":12.5,"llm_ms":45.6,"total_ms":58.1},"warnings":["profilo applicato"]}""";

    private const string LegacyResultJson = """{"text":"Risposta finale."}""";

    private const string ErrorDetailJson = """{"detail":"unable to decode audio"}""";

    private const string NewDetailErrorJson = """{"code":"internal","message":"transcription pipeline crashed"}""";

    private const string LegacyDetailListErrorJson =
        """{"detail":[{"loc":["body","profile"],"msg":"value is not a valid dictation profile","type":"value_error"}]}""";

    [Fact]
    public async Task Post_CarriesFormatQueryMultipartProfileAndNoModel()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { ResponseBody = DetailResultJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        byte[] pcm = PcmBytes(5000);
        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, pcm, "chat", "secret-token", UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Null(outcome.Error);

        FakeRestServer.CapturedRequest? captured = server.Captured;
        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method);
        Assert.Equal("/v1/audio/transcriptions", captured.Path);
        Assert.Equal("?format=pcm", captured.Query);
        Assert.Equal("Bearer secret-token", captured.Authorization);
        Assert.Equal(["profile"], captured.FormFields!.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray());

        IFormFile file = captured.File!;
        Assert.Equal("file", file.Name);
        Assert.Equal("dictation.pcm", file.FileName);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal(pcm.Length, file.Length);
        Assert.NotNull(captured.FileBytes);
        Assert.True(pcm.AsSpan().SequenceEqual(captured.FileBytes!));
    }

    [Fact]
    public async Task Post_OmitsAuthorizationHeaderWithoutToken()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { ResponseBody = LegacyResultJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "default", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Null(server.Captured!.Authorization);
        Assert.Equal("default", server.Captured.FormFields!["profile"]);
    }

    [Fact]
    public async Task ParsesLegacyResultWithOnlyText()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { ResponseBody = LegacyResultJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Null(outcome.Error);
        ResultMessage result = outcome.Result!;
        Assert.Equal("Risposta finale.", result.FinalText);
        Assert.Equal("Risposta finale.", result.RawText);
        Assert.False(result.FastPath);
        Assert.Equal(new DictationMetrics(0, 0, 0), result.Metrics);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ParsesFullDetailResult()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { ResponseBody = DetailResultJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        ResultMessage result = outcome.Result!;
        Assert.Equal("trascrizione grezza", result.RawText);
        Assert.Equal("Finale.", result.FinalText);
        Assert.True(result.FastPath);
        Assert.Equal(new DictationMetrics(12.5, 45.6, 58.1), result.Metrics);
        Assert.Equal(["profilo applicato"], result.Warnings);
    }

    [Theory]
    [InlineData(401, "unauthorized", false)]
    [InlineData(413, "payload_too_large", false)]
    [InlineData(422, "unexpected_response", false)]
    [InlineData(503, "busy", true)]
    [InlineData(504, "unexpected_response", true)]
    public async Task StatusError_SurfacesClassificationWithLegacyDetail(int status, string expectedCode, bool expectedRetryable)
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { Status = status, ResponseBody = ErrorDetailJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        DictationError error = outcome.Error!;
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(expectedRetryable, error.Retryable);
        Assert.Contains("unable to decode audio", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesNewDetailErrorEnvelope()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { Status = 500, ResponseBody = NewDetailErrorJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Error, outcome.Status);
        DictationError error = outcome.Error!;
        Assert.Equal("internal", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(500, error.HttpStatus);
        Assert.Contains("transcription pipeline crashed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesLegacyDetailListErrorMessage()
    {
        await using FakeRestServer server = await FakeRestServer.StartAsync(
            new FakeRestServer.Options { Status = 422, ResponseBody = LegacyDetailListErrorJson });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Error, outcome.Status);
        DictationError error = outcome.Error!;
        Assert.Equal(422, error.HttpStatus);
        Assert.Contains("value is not a valid dictation profile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadStall_TimesOutOnUploadDeadline_NotResponseDeadline()
    {
        FakeTimeProvider clock = new();
        ScriptedHttpHandler handler = new();
        handler.HangOnSend();
        using HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        RestDictationClient client = new(http, clock);

        Task<DictationOutcome> pending = client.TranscribeAsync(
            new Uri("http://rest/v1/audio/transcriptions", UriKind.Absolute),
            PcmBytes(8 * 1024 * 1024), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(6));

        DictationOutcome outcome = await pending;
        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        DictationError error = outcome.Error!;
        Assert.Equal("timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.Contains("upload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseStall_TimesOutOnResponseDeadline_NotUploadDeadline()
    {
        FakeTimeProvider clock = new();
        ScriptedHttpHandler handler = new();
        handler.HangOnBodyRead();
        using HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        RestDictationClient client = new(http, clock);

        Task<DictationOutcome> pending = client.TranscribeAsync(
            new Uri("http://rest/v1/audio/transcriptions", UriKind.Absolute),
            PcmBytes(2000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(16));

        DictationOutcome outcome = await pending;
        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        DictationError error = outcome.Error!;
        Assert.Equal("timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.Contains("response", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCancellation_ClosesRequestAndReturnsAbortedOutcome()
    {
        ScriptedHttpHandler handler = new();
        handler.HangOnSend();
        using HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        RestDictationClient client = new(http, new FakeTimeProvider());

        using CancellationTokenSource source = new();
        Task<DictationOutcome> pending = client.TranscribeAsync(
            new Uri("http://rest/v1/audio/transcriptions", UriKind.Absolute),
            PcmBytes(2000), "chat", null, UploadTimeout, ResponseTimeout, source.Token);

        source.Cancel();

        DictationOutcome outcome = await pending;
        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        DictationError error = outcome.Error!;
        Assert.Equal("aborted", error.Code);
        Assert.False(error.Retryable);

        await handler.CancelObserved.Task.WaitAsync(GuardTimeout);
    }

    [Fact]
    public async Task ResponseBody_LimitedToOneMiB()
    {
        Assert.Equal(1_048_576, RestDictationClient.MaxResponseBytes);

        await using FakeRestServer server = await FakeRestServer.StartAsync(new FakeRestServer.Options
        {
            Handler = context =>
            {
                byte[] payload = new byte[3 * 1024 * 1024];
                payload.AsSpan().Fill((byte)'a');
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength = payload.Length;
                return context.Response.Body.WriteAsync(payload).AsTask();
            },
        });
        using HttpClient http = CreateClient();
        RestDictationClient client = new(http, new FakeTimeProvider());

        DictationOutcome outcome = await client.TranscribeAsync(
            server.Url, PcmBytes(1000), "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Error, outcome.Status);
        DictationError error = outcome.Error!;
        Assert.Equal("protocol", error.Code);
        Assert.False(error.Retryable);
        Assert.Contains("1 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipartBody_CarriesProfileAndOctetStreamAndNoModelField()
    {
        ScriptedHttpHandler handler = new();
        handler.CaptureRawRequestAndRespond(LegacyResultJson);
        using HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        RestDictationClient client = new(http, new FakeTimeProvider());

        byte[] pcm = PcmBytes(50);
        DictationOutcome outcome = await client.TranscribeAsync(
            new Uri("http://rest/", UriKind.Absolute),
            pcm, "chat", null, UploadTimeout, ResponseTimeout, CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        string? contentType = handler.LastContentType;
        Assert.NotNull(contentType);
        Assert.StartsWith("multipart/form-data", contentType, StringComparison.Ordinal);

        string raw = handler.RawRequestBody!;
        Assert.Contains("name=\"file\"", raw, StringComparison.Ordinal);
        Assert.Contains("filename=\"dictation.pcm\"", raw, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/octet-stream", raw, StringComparison.Ordinal);
        Assert.Contains("name=\"profile\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"model\"", raw, StringComparison.Ordinal);

        string boundary = contentType![(contentType.IndexOf("boundary=", StringComparison.Ordinal) + "boundary=".Length)..];
        Assert.True(HasLineStartingBoundary(raw, boundary));
        Assert.True(HasLineStartingBoundary(raw, boundary + "--"));
    }

    private static bool HasLineStartingBoundary(string raw, string boundary)
    {
        return raw.Split("\r\n", StringSplitOptions.None).Any(line => line.StartsWith($"--{boundary}", StringComparison.Ordinal));
    }

    private static HttpClient CreateClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    private static byte[] PcmBytes(int length)
    {
        byte[] pcm = new byte[length];
        new Random(20261026).NextBytes(pcm);
        return pcm;
    }
}

internal sealed class FakeRestServer : IAsyncDisposable
{
    public sealed record CapturedRequest
    {
        public string? Method { get; set; }
        public string? Path { get; set; }
        public string? Query { get; set; }
        public string? Authorization { get; set; }
        public IReadOnlyDictionary<string, string>? FormFields { get; set; }

        public byte[]? FileBytes { get; set; }

        public IFormFile? File { get; set; }
    }

    public sealed record Options
    {
        public int? Status { get; init; }

        public string? ResponseBody { get; init; }

        public Func<HttpContext, Task>? Handler { get; init; }
    }

    public const string TranscriptionsPath = "/v1/audio/transcriptions";

    private readonly Options _options;
    private readonly TaskCompletionSource<CapturedRequest> _requestSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _requestAbortedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebApplication? _app;

    private FakeRestServer(Options options) => _options = options;

    public Uri Url { get; private set; } = null!;

    public CapturedRequest? Captured { get; set; }

    public Task<CapturedRequest> RequestReceived => _requestSource.Task;

    public Task RequestAborted => _requestAbortedSource.Task;

    public static async Task<FakeRestServer> StartAsync(Options? options = null)
    {
        FakeRestServer server = new(options ?? new Options());
        await server.StartCoreAsync();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task StartCoreAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        WebApplication app = builder.Build();
        app.Map(TranscriptionsPath, HandleAsync);

        await app.StartAsync();

        IServer kestrel = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = kestrel.Features.Get<IServerAddressesFeature>();
        string address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not expose a listening address.");
        Url = new Uri(address);
        _app = app;
    }

    private async Task HandleAsync(HttpContext context)
    {
        CapturedRequest captured = new()
        {
            Method = context.Request.Method,
            Path = context.Request.Path.Value,
            Query = context.Request.QueryString.Value,
            Authorization = ReadAuthorization(context),
        };
        Captured = captured;
        _requestSource.TrySetResult(captured);

        try
        {
            if (_options.Handler is { } handler)
            {
                await handler(context);
            }
            else
            {
                await HandleDefaultAsync(context, captured);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        finally
        {
            context.RequestAborted.Register(() => _requestAbortedSource.TrySetResult());
        }
    }

    private async Task HandleDefaultAsync(HttpContext context, CapturedRequest captured)
    {
        IFormCollection form = await context.Request.ReadFormAsync();
        captured.FormFields = form.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToString(),
            StringComparer.Ordinal);
        captured.File = form.Files.FirstOrDefault();
        if (captured.File is { } file)
        {
            byte[] bytes = new byte[file.Length];
            using (Stream stream = file.OpenReadStream())
            {
                await stream.ReadExactlyAsync(bytes);
            }

            captured.FileBytes = bytes;
        }

        context.Response.StatusCode = _options.Status ?? StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(_options.ResponseBody ?? "{}");
    }

    private static string? ReadAuthorization(HttpContext context)
    {
        return context.Request.Headers.TryGetValue("Authorization", out StringValues values)
            && values.Count > 0 ? values[0] : null;
    }
}

/// <summary>
/// Handler HTTP di prova: controlla esattamente quando la richiesta si completa, così le deadline
/// del client (timer del TimeProvider iniettato) scattano in modo deterministico, senza socket reali.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _send;

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastContentType { get; private set; }

    public string? RawRequestBody { get; private set; }

    public TaskCompletionSource<bool> CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void HangOnSend()
    {
        _send = HangOnSendCoreAsync;
    }

    public void HangOnBodyRead()
    {
        _send = (request, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangOnReadStream()),
        });
    }

    public void CaptureRawRequestAndRespond(string json)
    {
        _send = async (request, ct) =>
        {
            RawRequestBody = request.Content is { } content
                ? await content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        };
    }

    private async Task<HttpResponseMessage> HangOnSendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<HttpResponseMessage> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() =>
               {
                   CancelObserved.TrySetResult(true);
                   never.TrySetCanceled();
               }))
        {
            await never.Task.ConfigureAwait(false);
        }

        throw new UnreachableException();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastContentType = request.Content?.Headers.ContentType?.ToString();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = _send;
        if (send is null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        return await send(request, cancellationToken);
    }
}

internal sealed class HangOnReadStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<int> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
            never);
        try
        {
            return await never.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("The scripted REST response body was cancelled.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() { }
}
