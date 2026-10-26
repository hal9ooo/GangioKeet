using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class HealthMonitorTests
{
    private static readonly Uri BaseUri = new("http://localhost:8000");

    [Fact]
    public async Task HealthOk_ReportsReachableWithoutReady()
    {
        FakeHealthServer server = new()
        {
            HealthResponse = """{"status":"ok"}""",
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, token: null, time, TimeSpan.FromSeconds(30));

        HealthStatus? observed = null;
        monitor.Changed += s => observed = s;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.NotNull(observed);
        Assert.True(observed.Value.Reachable);
        Assert.Null(observed.Value.Ready);
        Assert.Null(observed.Value.Busy);
        Assert.Null(observed.Value.Draining);
    }

    [Fact]
    public async Task HealthOkWithReady_ReportsReadyState()
    {
        FakeHealthServer server = new()
        {
            HealthResponse = """{"status":"ok"}""",
            ReadyResponse = """{"status":"ready"}""",
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, token: null, time, TimeSpan.FromSeconds(30));

        HealthStatus? observed = null;
        monitor.Changed += s => observed = s;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.NotNull(observed);
        Assert.True(observed.Value.Reachable);
        Assert.True(observed.Value.Ready);
        Assert.False(observed.Value.Busy);
        Assert.False(observed.Value.Draining);
    }

    [Fact]
    public async Task ReadyNotFound_DoesNotMeanOffline()
    {
        FakeHealthServer server = new()
        {
            HealthResponse = """{"status":"ok"}""",
            ReadyStatusCode = 404,
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, token: null, time, TimeSpan.FromSeconds(30));

        HealthStatus? observed = null;
        monitor.Changed += s => observed = s;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.NotNull(observed);
        Assert.True(observed.Value.Reachable);
        Assert.Null(observed.Value.Ready);
    }

    [Fact]
    public async Task NetworkTimeout_ReportsOffline()
    {
        FakeHealthServer server = new()
        {
            HealthStatusCode = 500,
            HealthResponse = "Internal Server Error",
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, token: null, time, TimeSpan.FromSeconds(30));

        HealthStatus? observed = null;
        monitor.Changed += s => observed = s;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.NotNull(observed);
        Assert.False(observed.Value.Reachable);
    }

    [Fact]
    public async Task ReadyBusy_ReportsBusyButReachable()
    {
        FakeHealthServer server = new()
        {
            HealthResponse = """{"status":"ok"}""",
            ReadyResponse = """{"status":"busy"}""",
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, token: null, time, TimeSpan.FromSeconds(30));

        HealthStatus? observed = null;
        monitor.Changed += s => observed = s;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.NotNull(observed);
        Assert.True(observed.Value.Reachable);
        Assert.False(observed.Value.Ready);
        Assert.True(observed.Value.Busy);
        Assert.False(observed.Value.Draining);
    }

    [Fact]
    public async Task Token_IsSentWhenConfigured()
    {
        string? receivedToken = null;
        FakeHealthServer server = new()
        {
            HealthResponse = """{"status":"ok"}""",
            OnHealthRequest = msg => receivedToken = msg.Headers.Authorization?.ToString(),
        };

        using HttpClient http = new(server);
        FakeTimeProvider time = new();
        HealthMonitor monitor = new(http, BaseUri, "test-token-123", time, TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await monitor.RunAsync(cts.Token);

        Assert.Equal("Bearer test-token-123", receivedToken);
    }

    /// <summary>
    /// Fake HTTP handler that responds to /health and optionally /ready endpoints.
    /// </summary>
    private sealed class FakeHealthServer : HttpMessageHandler
    {
        public string HealthResponse { get; set; } = """{"status":"ok"}""";
        public int HealthStatusCode { get; set; } = 200;
        public TimeSpan HealthDelay { get; set; } = TimeSpan.Zero;

        public string? ReadyResponse { get; set; }
        public int ReadyStatusCode { get; set; } = 200;
        public TimeSpan ReadyDelay { get; set; } = TimeSpan.Zero;

        public Action<HttpRequestMessage>? OnHealthRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";

            if (path == "/ready")
            {
                if (ReadyResponse is null)
                {
                    // Nessun /ready configurato: simula 404
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                await Task.Delay(ReadyDelay, cancellationToken);
                return new HttpResponseMessage((HttpStatusCode)ReadyStatusCode)
                {
                    Content = new StringContent(ReadyResponse),
                };
            }

            // Default: /health
            if (HealthDelay > TimeSpan.Zero)
            {
                await Task.Delay(HealthDelay, cancellationToken);
            }

            OnHealthRequest?.Invoke(request);
            return new HttpResponseMessage((HttpStatusCode)HealthStatusCode)
            {
                Content = new StringContent(HealthResponse),
            };
        }
    }
}
