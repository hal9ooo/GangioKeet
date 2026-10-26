using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GangioKeet.Client.Tests.TestServer;

public sealed record ReceivedClientFrame(byte[] Data, WebSocketMessageType MessageType)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

public sealed class FakeDictationServer : IAsyncDisposable
{
    public sealed record Options
    {
        public int? HandshakeStatus { get; init; }
    }

    public const string DictationPath = "/ws/dictate";

    private readonly Options _options;
    private readonly TaskCompletionSource<FakeWsSession> _sessionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;

    private FakeDictationServer(Options options) => _options = options;

    public Uri Url { get; private set; } = null!;

    public FakeWsSession? Session { get; private set; }

    public static async Task<FakeDictationServer> StartAsync(Options? options = null)
    {
        FakeDictationServer server = new(options ?? new Options());
        await server.StartCoreAsync();
        return server;
    }

    public async Task<FakeWsSession> WaitForSessionAsync(TimeSpan timeout)
    {
        using CancellationTokenSource timeoutCts = new(timeout);
        return await _sessionSource.Task.WaitAsync(timeoutCts.Token);
    }

    private async Task StartCoreAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Map(DictationPath, HandleUpgradeAsync);

        await app.StartAsync();

        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
        string address = addresses?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not expose a listening address.");
        Url = new Uri(address);
        _app = app;
    }

    private async Task HandleUpgradeAsync(HttpContext context)
    {
        if (_options.HandshakeStatus is int status)
        {
            context.Response.StatusCode = status;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        context.Request.Headers.TryGetValue("Authorization", out var authorization);
        string? authorizationHeader = string.IsNullOrEmpty(authorization) ? null : authorization.ToString();
        FakeWsSession session = new(socket, authorizationHeader);
        Session = session;
        _sessionSource.TrySetResult(session);

        try
        {
            await session.RunAsync(context.RequestAborted);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            session.Complete();
            session.AbortSocket();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}

public sealed class FakeWsSession
{
    private readonly WebSocket _socket;
    private readonly Channel<ReceivedClientFrame> _inbound =
        Channel.CreateUnbounded<ReceivedClientFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    internal FakeWsSession(WebSocket socket, string? authorization)
    {
        _socket = socket;
        Authorization = authorization;
    }

    public string? Authorization { get; }

    public WebSocketState State => _socket.State;

    internal async Task RunAsync(CancellationToken stopping)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream message = new();
        while (!stopping.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, stopping);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                _inbound.Writer.TryWrite(new ReceivedClientFrame(message.ToArray(), result.MessageType));
                message.SetLength(0);
            }
        }

        _inbound.Writer.TryComplete();
    }

    public async Task<ReceivedClientFrame> ReadClientFrameAsync(CancellationToken ct = default)
    {
        return await _inbound.Reader.ReadAsync(ct);
    }

    public Task SendTextAsync(string json, CancellationToken ct = default)
    {
        return SendTextFragmentedAsync([json], ct);
    }

    public async Task SendTextFragmentedAsync(IReadOnlyList<string> fragments, CancellationToken ct = default)
    {
        for (int index = 0; index < fragments.Count; index++)
        {
            bool endOfMessage = index == fragments.Count - 1;
            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(fragments[index]),
                WebSocketMessageType.Text,
                endOfMessage,
                ct);
        }
    }

    public async ValueTask SendBinaryAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        await _socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct);
    }

    public Task CloseConnectionAsync(int closeCode, string reason, CancellationToken ct = default)
    {
        return _socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, reason, ct);
    }

    internal void Complete()
    {
        _inbound.Writer.TryComplete();
    }

    internal void AbortSocket()
    {
        try
        {
            _socket.Dispose();
        }
        catch (WebSocketException)
        {
        }
    }
}
