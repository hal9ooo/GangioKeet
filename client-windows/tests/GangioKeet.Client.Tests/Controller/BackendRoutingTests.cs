using System.Net;
using System.Text;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Tests.Fakes;
using Xunit;

namespace GangioKeet.Client.Tests.Controller;

public sealed class BackendRoutingTests
{
    private static readonly StartMessage Start = new("GangioKeet.Client", "Blocco note", "chat");

    [Fact]
    public async Task RemoteTurnPinsTheEndpointSelectedAtCreation()
    {
        MutableResolver resolver = new(Endpoint("primo", "10.0.0.1"));
        FakeSession firstSession = new();
        FakeSession secondSession = new();
        Queue<FakeSession> sessions = new([firstSession, secondSession]);
        RemoteDictationBackend backend = new(
            () => sessions.Dequeue(),
            resolver,
            new ClientConfig(),
            TimeProvider.System);

        await using IActiveDictationTurn first = backend.CreateTurn(Start);
        resolver.Set(Endpoint("secondo", "10.0.0.2"));
        await using IActiveDictationTurn second = backend.CreateTurn(Start);

        Assert.True((await first.StartAsync(default)).Ready);
        Assert.True((await second.StartAsync(default)).Ready);
        Assert.Equal("ws://10.0.0.1:8000/ws/dictate", firstSession.LastUri!.AbsoluteUri);
        Assert.Equal("ws://10.0.0.2:8000/ws/dictate", secondSession.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task RemoteRetryUsesRestAtTheCurrentlyActiveEndpoint()
    {
        MutableResolver resolver = new(Endpoint("primo", "10.0.0.1"));
        CapturingHandler handler = new();
        using HttpClient http = new(handler);
        RemoteDictationBackend backend = new(
            () => throw new InvalidOperationException("Retry must not open a WebSocket session."),
            resolver,
            new ClientConfig(),
            TimeProvider.System,
            () => http);

        DictationOutcome outcome = await backend.RetryAsync(new byte[] { 1, 2 }, Start, default);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Equal("Ciao?", outcome.Result!.FinalText);
        Assert.Equal("http://10.0.0.1:8000/v1/audio/transcriptions?format=pcm", handler.RequestUri!.AbsoluteUri);
    }

    private static EndpointConfig Endpoint(string name, string host) => new()
    {
        Name = name,
        Role = name == "primo" ? "primary" : "secondary",
        Host = host,
        Port = 8000,
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"raw_text":"ciao","final_text":"Ciao?"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
