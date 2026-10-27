using System.Net.Http;
using System.Text;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class EndpointProbeTests
{
    private static EndpointConfig Endpoint() => new()
    {
        Name = "Casa",
        Role = "primary",
        Host = "127.0.0.1",
        Port = 8000,
    };

    private static HttpResponseMessage Json(string body, System.Net.HttpStatusCode code = System.Net.HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Route(HttpRequestMessage request, FakeTimeProvider time, string? readyJson = null)
    {
        time.Advance(TimeSpan.FromMilliseconds(12));
        if (request.RequestUri!.AbsolutePath == "/health")
        {
            return Json("""{"status":"ok"}""");
        }

        return readyJson is null
            ? Json("""{"status":"ready"}""")
            : Json(readyJson);
    }

    [Fact]
    public async Task HealthOk_ReportsLatency()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(request => Route(request, time));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.True(result.Reachable);
        Assert.Equal(12d, result.LatencyMs!.Value, 3);
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task HealthFailure_ReportsHttpCode()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(_ => Json("""{"status":"down"}""", System.Net.HttpStatusCode.ServiceUnavailable));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.False(result.Reachable);
        Assert.Equal("http_503", result.ErrorCode);
    }

    [Fact]
    public async Task HealthTimeout_IsReportedAsTimeout()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(_ => throw new TaskCanceledException());
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.False(result.Reachable);
        Assert.Equal("timeout", result.ErrorCode);
    }

    [Fact]
    public async Task ReadyNotFound_DoesNotMeanOffline()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("""{"status":"ok"}""")
            : Json("""{"detail":"not found"}""", System.Net.HttpStatusCode.NotFound));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.True(result.Reachable);
        Assert.Null(result.Ready);
    }

    [Fact]
    public async Task ReadyBusy_IsExposed()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("""{"status":"ok"}""")
            : Json("""{"status":"busy"}"""));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.False(result.Ready);
        Assert.True(result.Busy);
        Assert.False(result.Draining);
    }

    [Fact]
    public async Task MalformedReady_IsIgnored()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(request => request.RequestUri!.AbsolutePath == "/health"
            ? Json("""{"status":"ok"}""")
            : Json("not-json"));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, token: null, time);

        ProbeResult result = await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        Assert.True(result.Reachable);
        Assert.Null(result.Ready);
        Assert.Null(result.Busy);
    }

    [Fact]
    public async Task Token_IsSentOnHealthRequest()
    {
        FakeTimeProvider time = new();
        DelegateHandler handler = new(request => Route(request, time));
        using HttpClient http = new(handler);
        EndpointProbe probe = new(http, "test-token", time);

        await probe.ProbeAsync(Endpoint(), TimeSpan.FromSeconds(2), default);

        HttpRequestMessage health = handler.Requests.First(r => r.RequestUri!.AbsolutePath == "/health");
        Assert.Equal("Bearer", health.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", health.Headers.Authorization.Parameter);
    }
}
