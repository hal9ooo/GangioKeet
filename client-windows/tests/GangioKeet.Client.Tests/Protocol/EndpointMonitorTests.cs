using System.Net.Http;
using System.Text;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class EndpointMonitorTests
{
    private static EndpointConfig Endpoint(string name, string host) => new()
    {
        Name = name,
        Role = name == "a" ? "primary" : "secondary",
        Host = host,
        Port = 8000,
    };

    private static HttpResponseMessage Ok()
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok"}""", Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task FirstRound_IsImmediate_ThenRepeatsAtInterval()
    {
        FakeTimeProvider time = new();
        int calls = 0;
        DelegateHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health")
            {
                Interlocked.Increment(ref calls);
            }

            return Ok();
        });
        using HttpClient http = new(handler);
        EndpointSelector selector = new(
            [Endpoint("a", "10.0.0.1"), Endpoint("b", "10.0.0.2")],
            25);
        using EndpointMonitor monitor = new(
            new EndpointProbe(http, token: null, time),
            selector,
            time,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));
        using CancellationTokenSource cts = new();

        Task loop = monitor.RunAsync(cts.Token);

        Assert.Equal(2, calls);

        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Assert.Equal(4, calls);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task Failure_IsPublishedToSelector()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using HttpClient http = new(handler);
        EndpointConfig endpoint = Endpoint("a", "10.0.0.1");
        EndpointSelector selector = new([endpoint], 25);
        using EndpointMonitor monitor = new(
            new EndpointProbe(http, token: null, time),
            selector,
            time,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));

        await monitor.ProbeOnceAsync(CancellationToken.None);

        EndpointStats stats = selector.StatsFor(endpoint);
        Assert.True(stats.Measured);
        Assert.False(stats.Reachable);
        Assert.Equal("http_503", stats.LastError);
    }

    [Fact]
    public async Task ProbeException_DoesNotKillTheLoop()
    {
        FakeTimeProvider time = new();
        int calls = 0;
        DelegateHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/health"
                && Interlocked.Increment(ref calls) == 1)
            {
                throw new ObjectDisposedException("http");
            }

            return Ok();
        });
        using HttpClient http = new(handler);
        EndpointConfig endpoint = Endpoint("a", "10.0.0.1");
        EndpointSelector selector = new([endpoint], 25);
        using EndpointMonitor monitor = new(
            new EndpointProbe(http, token: null, time),
            selector,
            time,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));
        using CancellationTokenSource cts = new();

        Task loop = monitor.RunAsync(cts.Token);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(selector.StatsFor(endpoint).Measured);

        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(selector.StatsFor(endpoint).Reachable);

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task ProbeOnce_WaitsForTheRoundInFlight()
    {
        FakeTimeProvider time = new();
        GatedHandler handler = new();
        using HttpClient http = new(handler);
        EndpointConfig endpoint = Endpoint("a", "10.0.0.1");
        EndpointSelector selector = new([endpoint], 25);
        using EndpointMonitor monitor = new(
            new EndpointProbe(http, token: null, time),
            selector,
            time,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));
        using CancellationTokenSource cts = new();

        Task loop = monitor.RunAsync(cts.Token);
        await handler.FirstStarted;

        Task once = monitor.ProbeOnceAsync(CancellationToken.None);
        await Task.Delay(50);

        // Il round periodico è ancora in volo: quello ad-hoc non deve
        // sovrapporsi sullo stesso selettore.
        Assert.Equal(1, handler.Calls);

        handler.Release();
        await once;

        Assert.Equal(2, handler.Calls);

        cts.Cancel();
        await loop;
    }

    /// <summary>Handler che blocca la prima richiesta finché il test non la rilascia.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstStarted => _firstStarted.Task;
        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/health")
            {
                return Ok();
            }

            int call = Interlocked.Increment(ref _calls);

            if (call == 1)
            {
                _firstStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return Ok();
        }
    }
}
