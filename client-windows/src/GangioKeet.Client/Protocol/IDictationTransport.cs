namespace GangioKeet.Client.Protocol;

public interface IDictationTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string? bearerToken, CancellationToken ct);

    Task SendTextAsync(string json, CancellationToken ct);

    Task SendBinaryAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    Task<ServerFrame> ReceiveAsync(CancellationToken ct);

    Task CloseAsync(CancellationToken ct);

    void Abort();
}

public sealed record ConnectFailure(string Code, string Message, int? HttpStatus);

public sealed class TransportConnectException : Exception
{
    public TransportConnectException(ConnectFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }

    public TransportConnectException(ConnectFailure failure, Exception innerException)
        : base(failure.Message, innerException)
    {
        Failure = failure;
    }

    public ConnectFailure Failure { get; }
}
