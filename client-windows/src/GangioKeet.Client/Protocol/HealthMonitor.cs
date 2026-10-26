using System.Net.Http;
using System.Text.Json;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Monitor periodico dello stato di salute del server. Interroga /health
/// (obbligatorio) e /ready (opzionale) e pubblica <see cref="Changed"/> ogni
/// volta che lo stato cambia. Una risposta assente o timeout su /health è
/// considerata offline; /ready 404 viene ignorato (non significa offline).
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string? _token;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _disposeCts = new();

    private HealthStatus _lastStatus;
    private bool _firstPoll = true;
    private Task? _loop;
    private bool _disposed;

    public HealthMonitor(
        HttpClient http,
        Uri baseUri,
        string? token,
        TimeProvider time,
        TimeSpan interval)
    {
        _http = http;
        _baseUri = baseUri;
        _token = token;
        _time = time;
        _interval = interval;
    }

    /// <summary>Evento pubblicato a ogni cambiamento dello stato di salute.</summary>
    public event Action<HealthStatus>? Changed;

    /// <summary>
    /// Avvia il loop di polling. Il primo poll avviene immediatamente; i
    /// successivi seguono l'intervallo configurato. Si ferma quando il token
    /// viene cancellato o <see cref="Dispose"/> viene chiamato.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_loop is not null)
        {
            return _loop;
        }

        _loop = RunLoopAsync(CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token).Token);
        return _loop;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Primo poll immediato
        await PollAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PollAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            HealthStatus status = await FetchHealthAsync(ct).ConfigureAwait(false);

            if (_firstPoll || !status.Equals(_lastStatus))
            {
                _firstPoll = false;
                _lastStatus = status;
                Changed?.Invoke(status);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown normale
        }
    }

    private async Task<HealthStatus> FetchHealthAsync(CancellationToken ct)
    {
        bool reachable;
        bool? ready = null;
        bool? busy = null;
        bool? draining = null;

        try
        {
            using var healthReq = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/health"));
            ApplyToken(healthReq);

            using HttpResponseMessage healthResp = await _http
                .SendAsync(healthReq, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            reachable = healthResp.IsSuccessStatusCode;

            // /ready è opzionale: 404 non è un errore
            try
            {
                using var readyReq = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/ready"));
                ApplyToken(readyReq);

                using HttpResponseMessage readyResp = await _http
                    .SendAsync(readyReq, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                if (readyResp.IsSuccessStatusCode)
                {
                    string body = await readyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using JsonDocument doc = JsonDocument.Parse(body);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("status", out JsonElement statusEl))
                    {
                        string status = statusEl.GetString() ?? "";
                        ready = status == "ready";
                        busy = status == "busy";
                        draining = status == "draining";
                    }
                }
                // 404 o altro errore su /ready = server senza /ready, non offline
            }
            catch (HttpRequestException)
            {
                // /ready non disponibile, ignorato
            }
        }
        catch (HttpRequestException)
        {
            reachable = false;
        }
        catch (TaskCanceledException)
        {
            reachable = false;
        }

        return new HealthStatus(reachable, ready, busy, draining);
    }

    private void ApplyToken(HttpRequestMessage request)
    {
        if (_token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }
    }
}
