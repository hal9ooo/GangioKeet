using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Protocol;
using Xunit;

namespace GangioKeet.Client.Tests.Integration;

/// <summary>
/// Test contro il server Mac reale. Richiedono RUN_REAL_SERVER=1 e un server
/// raggiungibile su GANGIOKEET_HOST (default 192.168.1.167:8000).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RealServerTests
{
    private static Uri BaseUri
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable("GANGIOKEET_HOST")
                ?? "192.168.1.167:8000";
            return raw.Contains("://", StringComparison.Ordinal)
                ? new Uri(raw)
                : new Uri($"http://{raw}");
        }
    }

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_REAL_SERVER") == "1";

    private static byte[] LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "it_short.pcm");
        Skip.IfNot(File.Exists(path), $"fixture non trovata: {path}");
        return File.ReadAllBytes(path);
    }

    [SkippableFact]
    public async Task Health_ReturnsOk()
    {
        Skip.IfNot(Enabled, "RUN_REAL_SERVER != 1");
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };

        using HttpResponseMessage response = await http.GetAsync(new Uri(BaseUri, "/health"));

        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
    }

    [SkippableFact]
    public async Task RestPcm_ReturnsResultWithNonEmptyText()
    {
        Skip.IfNot(Enabled, "RUN_REAL_SERVER != 1");
        byte[] pcm = LoadFixture();

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        RestDictationClient client = new(http, TimeProvider.System);

        DictationOutcome outcome = await client.TranscribeAsync(
            BaseUri,
            pcm,
            "default",
            token: null,
            uploadTimeout: TimeSpan.FromSeconds(10),
            responseTimeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Result!.RawText));
    }

    [SkippableFact]
    public async Task RestSilence_DoesNotCrashAndReturnsAResultShape()
    {
        Skip.IfNot(Enabled, "RUN_REAL_SERVER != 1");
        // 1 s di silenzio PCM16 mono 16 kHz
        byte[] silence = new byte[32000];

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        RestDictationClient client = new(http, TimeProvider.System);

        DictationOutcome outcome = await client.TranscribeAsync(
            BaseUri,
            silence,
            "default",
            token: null,
            uploadTimeout: TimeSpan.FromSeconds(10),
            responseTimeout: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // Il silenzio può produrre un result con testo vuoto o un errore: mai un crash.
        Assert.True(
            outcome.Status is DictationStatus.Result or DictationStatus.Error,
            $"Status inatteso: {outcome.Status}");
    }

    [SkippableFact]
    public async Task WebSocket_PcmRoundTripProducesAResult()
    {
        Skip.IfNot(Enabled, "RUN_REAL_SERVER != 1");
        byte[] pcm = LoadFixture();

        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Uri wsUri = new($"ws://{BaseUri.Authority}/ws/dictate");

        await transport.ConnectAsync(wsUri, bearerToken: null, cts.Token);

        StartMessage start = new("GangioKeet.Tests", "integration", "default");
        await transport.SendTextAsync(ProtocolJson.SerializeStart(start), cts.Token);

        int chunkSize = 3200;
        for (int offset = 0; offset < pcm.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, pcm.Length - offset);
            await transport.SendBinaryAsync(pcm.AsMemory(offset, count), cts.Token);
        }

        await transport.SendTextAsync(DictationSession.StopMessageJson, cts.Token);

        ServerFrame frame = await transport.ReceiveAsync(cts.Token);
        ResultFrame result = Assert.IsType<ResultFrame>(frame);
        Assert.False(string.IsNullOrWhiteSpace(result.Result.RawText));
    }

    [SkippableFact]
    public async Task WebSocket_AbortEndsWithoutResult()
    {
        Skip.IfNot(Enabled, "RUN_REAL_SERVER != 1");

        await using WebSocketDictationTransport transport = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        Uri wsUri = new($"ws://{BaseUri.Authority}/ws/dictate");

        await transport.ConnectAsync(wsUri, bearerToken: null, cts.Token);
        StartMessage start = new("GangioKeet.Tests", "integration", "default");
        await transport.SendTextAsync(ProtocolJson.SerializeStart(start), cts.Token);
        await transport.SendBinaryAsync(new byte[3200], cts.Token);
        await transport.SendTextAsync("{\"type\":\"abort\"}", cts.Token);

        // Il server può chiudere o restare in attesa: basta non bloccarsi per sempre.
        using CancellationTokenSource abortCts = new(TimeSpan.FromSeconds(5));
        try
        {
            ServerFrame frame = await transport.ReceiveAsync(abortCts.Token);
            Assert.True(frame is ResultFrame or ErrorFrame);
        }
        catch (OperationCanceledException)
        {
            // timeout accettabile: l'abort non promette un frame di risposta
        }
    }
}
