using System.Net.WebSockets;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Tests.TestServer;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class WebSocketTransportTests
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestOperationTimeout = TimeSpan.FromSeconds(10);

    private const string ExampleResultJson =
        """{"type":"result","raw_text":"raw speech","final_text":"Final speech.","fast_path":true,"metrics":{"stt_ms":12.5,"llm_ms":0,"total_ms":12.5}}""";

    private static string WebSocketUri(FakeDictationServer server, string path)
    {
        return new UriBuilder(server.Url)
        {
            Scheme = "ws",
            Path = path,
        }.Uri.ToString();
    }

    private static byte[] MakePcm(int length)
    {
        byte[] pcm = new byte[length];
        new Random(20261026).NextBytes(pcm);
        return pcm;
    }

    [Fact]
    public async Task RoundTripsBinaryAudioAndJsonText()
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        await transport.ConnectAsync(uri, null, operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);

        byte[] pcm = MakePcm(150_000);
        await transport.SendBinaryAsync(pcm, operationCts.Token);
        ReceivedClientFrame audioFrame = await session.ReadClientFrameAsync(operationCts.Token);

        Assert.Equal(WebSocketMessageType.Binary, audioFrame.MessageType);
        Assert.Equal(pcm.Length, audioFrame.Data.Length);
        Assert.True(pcm.AsSpan().SequenceEqual(audioFrame.Data));

        string startJson = ProtocolJson.SerializeStart(new StartMessage("demo", "Terminal Window", "code"));
        await transport.SendTextAsync(startJson, operationCts.Token);
        ReceivedClientFrame startFrame = await session.ReadClientFrameAsync(operationCts.Token);

        Assert.Equal(WebSocketMessageType.Text, startFrame.MessageType);
        Assert.Equal(startJson, startFrame.Text);

        await session.SendTextAsync(ExampleResultJson, operationCts.Token);
        ServerFrame frame = await transport.ReceiveAsync(operationCts.Token);

        ResultFrame resultFrame = Assert.IsType<ResultFrame>(frame);
        Assert.Equal("raw speech", resultFrame.Result.RawText);
        Assert.True(resultFrame.Result.FastPath);
    }

    [Fact]
    public async Task ReceiveAsync_ReassemblesFragmentedJson()
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        await transport.ConnectAsync(uri, null, operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);

        await session.SendTextFragmentedAsync(
        [
            """{"type":"result","raw_text":"raw sp""",
            """eech","final_text":"Final sp""",
            """eech.","fast_path":true,"metrics":{"stt_ms":12.5,"llm_ms":0,"total_ms":12.5}}""",
        ], operationCts.Token);

        ServerFrame frame = await transport.ReceiveAsync(operationCts.Token);

        ResultFrame resultFrame = Assert.IsType<ResultFrame>(frame);
        Assert.Equal("raw speech", resultFrame.Result.RawText);
        Assert.Equal("Final speech.", resultFrame.Result.FinalText);
        Assert.True(resultFrame.Result.FastPath);
    }

    [Fact]
    public async Task ReceiveAsync_RejectsJsonLargerThanOneMiB()
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        Assert.Equal(1_048_576, WebSocketDictationTransport.MaxJsonMessageBytes);

        await transport.ConnectAsync(uri, null, operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);

        const int oversizedLength = 1_200_000;
        string oversized = "{\"type\":\"result\",\"raw_text\":\"" + new string('a', oversizedLength) + "\"}";
        await session.SendTextAsync(oversized, operationCts.Token);

        Exception exception = await Record.ExceptionAsync(() => transport.ReceiveAsync(operationCts.Token));

        ProtocolFrameException protocolError = Assert.IsType<ProtocolFrameException>(exception);
        Assert.Contains("1 MiB", protocolError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1009, "payload_too_large")]
    [InlineData(1008, "policy_violation")]
    public async Task ReceiveAsync_SurfacesServerCloseCode(int closeCode, string reason)
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        await transport.ConnectAsync(uri, null, operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);
        await session.CloseConnectionAsync(closeCode, reason, operationCts.Token);

        Exception exception = await Record.ExceptionAsync(() => transport.ReceiveAsync(operationCts.Token));

        TransportClosedException closed = Assert.IsType<TransportClosedException>(exception);
        Assert.Equal(closeCode, closed.CloseCode);
        Assert.Equal(reason, closed.CloseReason);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(426, "upgrade_required")]
    [InlineData(200, "unexpected_response")]
    public async Task ConnectAsync_ClassifiesFailedHandshake(int handshakeStatus, string expectedCode)
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync(
            new FakeDictationServer.Options { HandshakeStatus = handshakeStatus });
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        Exception exception = await Record.ExceptionAsync(
            () => transport.ConnectAsync(uri, null, operationCts.Token));

        TransportConnectException connectError = Assert.IsType<TransportConnectException>(exception);
        Assert.Equal(expectedCode, connectError.Failure.Code);
        Assert.Equal(handshakeStatus, connectError.Failure.HttpStatus);
        Assert.Contains(handshakeStatus.ToString(), connectError.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_SendsBearerTokenWhenConfigured()
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        await transport.ConnectAsync(uri, "shared-secret-token", operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);

        Assert.Equal("Bearer shared-secret-token", session.Authorization);
    }

    [Fact]
    public async Task ConnectAsync_OmitsBearerHeaderWhenNotConfigured()
    {
        await using FakeDictationServer server = await FakeDictationServer.StartAsync();
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        await transport.ConnectAsync(uri, null, operationCts.Token);
        FakeWsSession session = await server.WaitForSessionAsync(SessionTimeout);

        Assert.Null(session.Authorization);
    }

    [Fact]
    public async Task ConnectAsync_NeverLogsTheBearerToken()
    {
        const string secretToken = "token-that-must-never-be-logged-4f8a";
        await using FakeDictationServer server = await FakeDictationServer.StartAsync(
            new FakeDictationServer.Options { HandshakeStatus = 401 });
        Uri uri = new(WebSocketUri(server, FakeDictationServer.DictationPath));
        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource operationCts = new(TestOperationTimeout);

        Exception exception = await Record.ExceptionAsync(
            () => transport.ConnectAsync(uri, secretToken, operationCts.Token));

        TransportConnectException connectError = Assert.IsType<TransportConnectException>(exception);
        Assert.DoesNotContain(secretToken, connectError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, connectError.Failure.Message, StringComparison.Ordinal);
    }
}
