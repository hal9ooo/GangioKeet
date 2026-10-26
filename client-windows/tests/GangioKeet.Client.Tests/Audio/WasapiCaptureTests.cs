using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using Xunit;
using Xunit.Abstractions;

namespace GangioKeet.Client.Tests.Audio;

[Trait("Category", "Integration")]
public sealed class WasapiCaptureTests
{
    private readonly ITestOutputHelper _output;

    public WasapiCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static void SkipUnlessDesktopTestRun()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");
    }

    [SkippableFact]
    public async Task RealDevices_EnumerationIsConsistent()
    {
        SkipUnlessDesktopTestRun();

        IReadOnlyList<AudioDeviceInfo> devices = await new AudioDevices().ListAsync(CancellationToken.None);
        Skip.IfNot(devices.Count > 0, "nessun dispositivo di input attivo su questo sistema");

        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        Assert.Single(devices, d => d.IsDefault);

        foreach (AudioDeviceInfo device in devices)
        {
            _output.WriteLine($"Id={device.Id}; Name={device.Name}; Default={device.IsDefault}");
        }
    }

    [SkippableFact]
    public async Task RealCapture_ProducesPcm16ChunksWithConsistentSummary()
    {
        SkipUnlessDesktopTestRun();

        MicrophonePrivacy privacy = new();
        Skip.IfNot(privacy.IsAccessAllowed(), "accesso microfono negato nelle impostazioni privacy di sistema");

        using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(60));
        CancellationToken ct = testTimeout.Token;

        IReadOnlyList<AudioDeviceInfo> devices = await new AudioDevices().ListAsync(ct);
        Skip.IfNot(devices.Count > 0, "nessun dispositivo di input attivo su questo sistema");

        await using WasapiAudioCapture capture = new(new AudioConfig());
        List<CaptureFailure> failures = [];
        List<ReadOnlyMemory<byte>> chunks = [];
        List<double> levels = [];
        int readyCount = 0;
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        object gate = new();
        capture.Ready += (_, _) =>
        {
            lock (gate)
            {
                readyCount++;
                ready.TrySetResult();
            }
        };
        capture.ChunkReady += (_, chunk) =>
        {
            lock (gate)
            {
                chunks.Add(chunk);
            }
        };
        capture.LevelChanged += (_, level) =>
        {
            lock (gate)
            {
                levels.Add(level);
            }
        };
        capture.Failed += (_, failure) =>
        {
            lock (gate)
            {
                failures.Add(failure);
            }
        };

        await capture.StartAsync(null, ct);

        System.Diagnostics.Stopwatch wallClock = System.Diagnostics.Stopwatch.StartNew();

        Task completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
        lock (gate)
        {
            if (readyCount == 0)
            {
                CaptureFailure published = failures.Count > 0
                    ? failures[0]
                    : new CaptureFailure("unknown", "Nessun callback audio entro 10 s.");
                Skip.If(true, $"microfono non pronto su questo sistema ({published.Code}): {published.Message}");
            }
        }

        // Raccoglie ~600 ms di audio reale prima dello stop.
        await Task.Delay(TimeSpan.FromMilliseconds(600), ct);

        AudioSummary summary = await capture.StopAsync(ct);
        wallClock.Stop();

        lock (gate)
        {
            Assert.True(
                    failures.Count == 0 || failures.All(f => f.Code is "device_lost"),
                    "failure inattesi durante la cattura hardware: " +
                    string.Join("; ", failures.Select(f => $"{f.Code}: {f.Message}")));

            Assert.Equal(1, readyCount);
            Assert.NotEmpty(levels);
            Assert.All(levels, level => Assert.InRange(level, 0, 1));

            // Ogni chunk: non vuoto, pari, mai oltre 3200 byte; tutti interi tranne il flush finale.
            Assert.All(chunks, c => Assert.True(c.Length > 0 && c.Length % 2 == 0 && c.Length <= 3200));
            foreach (ReadOnlyMemory<byte> chunk in chunks.Take(chunks.Count - 1))
            {
                Assert.True(chunk.Length == 3200, $"chunk intermedio da {chunk.Length} byte (atteso 3200)");
            }
        }

        byte[] joined;
        lock (gate)
        {
            joined = chunks.SelectMany(c => c.ToArray()).ToArray();
        }

        Assert.Equal(summary.SampleCount * 2, joined.Length);

        // La durata PCM deve corrispondere al tempo reale di registrazione: un resampler
        // che imbottisce silenzio produrrebbe molti più secondi dei campioni veri
        // (bug osservato: 2,4 s di PCM per 0,6 s di registrazione).
        double pcmSeconds = joined.Length / 32000.0;
        Assert.InRange(
            pcmSeconds,
            wallClock.Elapsed.TotalSeconds * 0.5,
            wallClock.Elapsed.TotalSeconds * 2.0);

        // La diagnosi segue esattamente i byte realmente consegnati al consumatore.
        AudioSignalTracker verifier = new();
        verifier.AppendPcm16(joined);
        AudioSummary recalculated = verifier.BuildSummary();

        _output.WriteLine(
            $"chunks={chunks.Count}; bytes={joined.Length}; delivered=({summary.SampleCount}, AllZero={summary.AllZero}, Peak={summary.Peak}); " +
            $"recalculated=({recalculated.SampleCount}, AllZero={recalculated.AllZero}, Peak={recalculated.Peak})");
        Assert.Equal(summary.SampleCount, recalculated.SampleCount);
        Assert.Equal(summary.AllZero, recalculated.AllZero);
        Assert.Equal(summary.Peak, recalculated.Peak);
    }
}
