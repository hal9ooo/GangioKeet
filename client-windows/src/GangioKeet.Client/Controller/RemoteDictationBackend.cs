using System.Net.Http;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

public sealed class RemoteDictationBackend(
    Func<IDictationSession> sessionFactory,
    IEndpointResolver endpoints,
    ClientConfig config,
    TimeProvider time,
    Func<HttpClient>? httpClientFactory = null) : IDictationBackend
{
    private readonly Func<HttpClient> _httpClientFactory =
        httpClientFactory ?? (static () => new HttpClient());

    public IActiveDictationTurn CreateTurn(StartMessage start) =>
        new RemoteDictationTurn(sessionFactory(), endpoints.Active, start, config);

    public async Task<DictationOutcome> RetryAsync(
        ReadOnlyMemory<byte> pcm,
        StartMessage start,
        CancellationToken ct)
    {
        using HttpClient http = _httpClientFactory();
        RestDictationClient rest = new(http, time);
        EndpointConfig endpoint = endpoints.Active;
        return await rest.TranscribeAsync(
            ServerEndpoint.HttpBase(endpoint),
            pcm,
            start.Profile,
            ApiCredential.Resolve(config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
            TimeSpan.FromSeconds(config.Server.UploadTimeoutSeconds),
            TimeSpan.FromSeconds(config.Server.RestResponseTimeoutSeconds),
            ct).ConfigureAwait(false);
    }
}

internal sealed class RemoteDictationTurn : IActiveDictationTurn
{
    private readonly IDictationSession _session;
    private readonly EndpointConfig _endpoint;
    private readonly StartMessage _start;
    private readonly ClientConfig _config;
    private Task<TurnStartOutcome>? _startTask;
    private int _disposed;

    public RemoteDictationTurn(
        IDictationSession session,
        EndpointConfig endpoint,
        StartMessage start,
        ClientConfig config)
    {
        _session = session;
        _endpoint = endpoint;
        _start = start;
        _config = config;
    }

    public string BackendName => _endpoint.Name;
    public Task<DictationOutcome> Completion => _session.Completion;

    public Task<TurnStartOutcome> StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _startTask ??= StartCoreAsync(ct);
    }

    public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _session.TryEnqueueAudio(ownedPcm);
    }

    public Task<DictationOutcome> FinishAsync(CancellationToken ct) =>
        _session.FinishAsync(ct);

    public Task AbortAsync(CancellationToken ct) =>
        _session.AbortAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<TurnStartOutcome> StartCoreAsync(CancellationToken ct)
    {
        SessionConnectOutcome outcome = await _session.ConnectAndStartAsync(
            ServerEndpoint.WebSocket(_endpoint),
            _start,
            ApiCredential.Resolve(_config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
            TimeSpan.FromSeconds(_config.Server.ConnectTimeoutSeconds),
            ct).ConfigureAwait(false);

        if (outcome.Connected) return new TurnStartOutcome(true, null);

        ConnectFailure failure = outcome.Failure
            ?? new ConnectFailure("connect_failed", "Server non raggiungibile", null);
        string message = failure.Code == "connect_failed"
            ? "Server non raggiungibile"
            : failure.Message;
        return new TurnStartOutcome(false, new DictationError(
            failure.Code,
            message,
            Retryable: false,
            failure.HttpStatus));
    }
}
