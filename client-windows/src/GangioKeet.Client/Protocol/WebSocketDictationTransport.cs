using System.Buffers;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace GangioKeet.Client.Protocol;

public sealed class TransportClosedException : Exception
{
    public TransportClosedException(int? closeCode, string? closeReason)
        : base(BuildMessage(closeCode))
    {
        CloseCode = closeCode;
        CloseReason = closeReason;
    }

    public TransportClosedException(int? closeCode, string? closeReason, Exception innerException)
        : base(BuildMessage(closeCode), innerException)
    {
        CloseCode = closeCode;
        CloseReason = closeReason;
    }

    public int? CloseCode { get; }

    public string? CloseReason { get; }

    private static string BuildMessage(int? closeCode)
    {
        return closeCode.HasValue
            ? $"WebSocket connection closed by the server with code {closeCode.Value}."
            : "WebSocket connection closed unexpectedly.";
    }
}

public sealed class WebSocketDictationTransport : IDictationTransport
{
    public const int MaxJsonMessageBytes = 1_048_576;

    private const int ChunkSize = 64 * 1024;

    private const string BearerPrefix = "Bearer ";

    private ClientWebSocket? _socket;

    public async Task ConnectAsync(Uri uri, string? bearerToken, CancellationToken ct)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("Transport is already connected.");
        }

        ClientWebSocket socket = new();
        socket.Options.CollectHttpResponseDetails = true;
        if (!string.IsNullOrEmpty(bearerToken))
        {
            socket.Options.SetRequestHeader("Authorization", BearerPrefix + bearerToken);
        }

        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (WebSocketException ex)
        {
            ConnectFailure failure = ClassifyFailedUpgrade(socket, ex);
            socket.Dispose();
            throw new TransportConnectException(failure, ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
    }

    public async Task SendTextAsync(string json, CancellationToken ct)
    {
        ClientWebSocket socket = ConnectedSocket();
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxJsonMessageBytes)
        {
            throw new ProtocolFrameException($"WebSocket JSON message exceeds the 1 MiB limit ({MaxJsonMessageBytes} bytes).");
        }

        await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        ClientWebSocket socket = ConnectedSocket();
        if (pcm.Length == 0)
        {
            await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            return;
        }

        for (int offset = 0; offset < pcm.Length; offset += ChunkSize)
        {
            int length = Math.Min(ChunkSize, pcm.Length - offset);
            bool endOfMessage = offset + length >= pcm.Length;
            await socket.SendAsync(pcm.Slice(offset, length), WebSocketMessageType.Binary, endOfMessage, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Riceve il frame JSON successivo, ri-assemblando gli eventuali frammenti.
    /// Contratto a morte dopo errore di protocollo: se (o quando) un <see cref="ProtocolFrameException"/>
    /// esce da questo metodo (messaggio JSON oversized o frame binario inatteso), i frammenti residui
    /// restano nel buffer del socket e una chiamata successiva può ri-assemblare dati incoerenti;
    /// dopo un errore di protocollo non bisogna riutilizzare il transport: chiamare <see cref="Abort"/>
    /// (o <see cref="DisposeAsync"/>) e riconnettere.
    /// </summary>
    public async Task<ServerFrame> ReceiveAsync(CancellationToken ct)
    {
        ClientWebSocket socket = ConnectedSocket();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using MemoryStream message = new();
            WebSocketMessageType? messageType = null;
            while (true)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await EchoCloseAsync(socket).ConfigureAwait(false);
                    int? closeCode = socket.CloseStatus is WebSocketCloseStatus status ? (int)status : null;
                    throw new TransportClosedException(closeCode, socket.CloseStatusDescription);
                }

                messageType ??= result.MessageType;
                message.Write(buffer, 0, result.Count);
                // Il limite vale per l'intero messaggio: verificato dopo ogni frammento,
                // incluso l'ultimo, prima di uscire dal ciclo.
                if (message.Length > MaxJsonMessageBytes)
                {
                    throw new ProtocolFrameException($"WebSocket JSON message exceeds the 1 MiB limit ({MaxJsonMessageBytes} bytes).");
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (messageType == WebSocketMessageType.Binary)
            {
                throw new ProtocolFrameException("Binary server frames are not part of the dictation protocol.");
            }

            string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            return ProtocolJson.ParseServerFrame(json);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            throw new TransportClosedException(null, ex.Message, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Abort()
    {
        _socket?.Abort();
    }

    public ValueTask DisposeAsync()
    {
        ClientWebSocket? socket = Interlocked.Exchange(ref _socket, null);
        socket?.Dispose();
        return ValueTask.CompletedTask;
    }

    private ClientWebSocket ConnectedSocket()
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Transport is not connected.");
        }

        return socket;
    }

    private static ConnectFailure ClassifyFailedUpgrade(ClientWebSocket socket, WebSocketException exception)
    {
        int httpStatus = (int)socket.HttpStatusCode;
        if (httpStatus is < 100 or > 599)
        {
            return new ConnectFailure(
                "connect_failed",
                "WebSocket connection failed before an HTTP handshake response was received.",
                null);
        }

        string message = $"WebSocket handshake rejected with HTTP {httpStatus}.";
        return httpStatus switch
        {
            401 => new ConnectFailure("unauthorized", message, httpStatus),
            403 => new ConnectFailure("forbidden", message, httpStatus),
            404 => new ConnectFailure("not_found", message, httpStatus),
            426 => new ConnectFailure("upgrade_required", message, httpStatus),
            _ => new ConnectFailure("unexpected_response", message, httpStatus),
        };
    }

    private static async Task EchoCloseAsync(ClientWebSocket socket)
    {
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
