using System.Net.Http;
using System.Text.Json;
using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Misura un endpoint con una richiesta <c>GET /health</c> cronometrata e legge
/// <c>/ready</c> quando disponibile. La latenza riguarda solo <c>/health</c>;
/// un <c>/ready</c> assente, in errore o malformato non rende l'endpoint
/// offline.
/// </summary>
public sealed class EndpointProbe
{
    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly TimeProvider _time;

    public EndpointProbe(HttpClient http, string? token, TimeProvider time)
    {
        _http = http;
        _token = token;
        _time = time;
    }

    public async Task<ProbeResult> ProbeAsync(
        EndpointConfig endpoint,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Uri baseUri = ServerEndpoint.HttpBase(endpoint);

        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        long start = _time.GetTimestamp();
        bool reachable;
        string? errorCode = null;

        try
        {
            using var healthRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/health"));
            ApplyToken(healthRequest);

            using HttpResponseMessage response = await _http
                .SendAsync(healthRequest, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);

            reachable = response.IsSuccessStatusCode;
            if (!reachable)
            {
                errorCode = $"http_{(int)response.StatusCode}";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, null, null, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, null, null, null, null, "unreachable");
        }

        double latencyMs = _time.GetElapsedTime(start).TotalMilliseconds;

        if (!reachable)
        {
            return new ProbeResult(false, null, null, null, null, errorCode);
        }

        (bool? ready, bool? busy, bool? draining) = await ReadReadyAsync(baseUri, linked.Token)
            .ConfigureAwait(false);

        return new ProbeResult(true, latencyMs, ready, busy, draining, null);
    }

    private async Task<(bool? Ready, bool? Busy, bool? Draining)> ReadReadyAsync(
        Uri baseUri,
        CancellationToken ct)
    {
        try
        {
            using var readyRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/ready"));
            ApplyToken(readyRequest);

            using HttpResponseMessage response = await _http
                .SendAsync(readyRequest, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("status", out JsonElement statusElement))
                {
                    return (null, null, null);
                }

                string status = statusElement.GetString() ?? "";
                return (status == "ready", status == "busy", status == "draining");
            }
            catch (JsonException)
            {
                return (null, null, null);
            }
        }
        catch (HttpRequestException)
        {
            return (null, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, null);
        }
    }

    private void ApplyToken(HttpRequestMessage request)
    {
        if (_token is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }
    }
}
