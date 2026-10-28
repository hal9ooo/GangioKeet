using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Paste;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;
using GangioKeet.Client.Tests.Fakes;
using Xunit;

namespace GangioKeet.Client.Tests.Controller;

public sealed class DictationControllerBackendSwapTests
{
    private sealed class TrackingBackend : IDictationBackend
    {
        public int TurnCount { get; private set; }
        public bool IsReady => true;

        public IActiveDictationTurn CreateTurn(StartMessage start)
        {
            TurnCount++;
            return new FakeTurn();
        }

        public Task<DictationOutcome> RetryAsync(ReadOnlyMemory<byte> pcm, StartMessage start, CancellationToken ct)
            => Task.FromResult(new DictationOutcome(DictationStatus.Result, new ResultMessage("raw", "final", false, new DictationMetrics(1, 2, 3), []), null));

        private sealed class FakeTurn : IActiveDictationTurn
        {
            public string BackendName => "tracking";

            public Task<TurnStartOutcome> StartAsync(CancellationToken ct)
                => Task.FromResult(new TurnStartOutcome(true, null));

            public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm) => true;

            public Task<DictationOutcome> Completion => Task.FromResult(new DictationOutcome(
                DictationStatus.Result,
                new ResultMessage("raw", "final", false, new DictationMetrics(1, 2, 3), []),
                null));

            public Task<DictationOutcome> FinishAsync(CancellationToken ct) => Completion;

            public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SetBackend_WhenIdle_UpdatesCurrentBackendImmediately()
    {
        var initialBackend = new TrackingBackend();
        var nextBackend = new TrackingBackend();

        var capture = new FakeCapture();
        var pill = new FakePillView();
        var sound = new FakeSoundPlayer();
        var config = new ClientConfig();
        var telemetry = new TelemetryStore();
        var time = TimeProvider.System;
        var recovery = new RecoveryStore(time, TimeSpan.FromMinutes(5));
        var paste = new PasteService(
            new Win32ClipboardService(),
            new SendInputSimulator(),
            new FakeForegroundWindowService(),
            new FakeElevationChecker(),
            new StaPasteDispatcher(),
            config.Paste,
            time);

        await using var controller = new DictationController(
            capture,
            initialBackend,
            paste,
            recovery,
            new FakeForegroundWindowService { Window = new ForegroundWindowInfo(1, 100, "TestApp", "TestTitle") },
            new FakeElevationChecker(),
            pill,
            sound,
            config,
            telemetry,
            time);

        Assert.Same(initialBackend, controller.CurrentBackend);

        controller.SetBackend(nextBackend);

        Assert.Same(nextBackend, controller.CurrentBackend);
    }

    [Fact]
    public async Task SetBackend_RoutesNextTurnToNewBackend()
    {
        var initialBackend = new TrackingBackend();
        var nextBackend = new TrackingBackend();

        var capture = new FakeCapture();
        var pill = new FakePillView();
        var sound = new FakeSoundPlayer();
        var config = new ClientConfig();
        var telemetry = new TelemetryStore();
        var time = TimeProvider.System;
        var recovery = new RecoveryStore(time, TimeSpan.FromMinutes(5));
        var paste = new PasteService(
            new Win32ClipboardService(),
            new SendInputSimulator(),
            new FakeForegroundWindowService(),
            new FakeElevationChecker(),
            new StaPasteDispatcher(),
            config.Paste,
            time);

        await using var controller = new DictationController(
            capture,
            initialBackend,
            paste,
            recovery,
            new FakeForegroundWindowService { Window = new ForegroundWindowInfo(1, 100, "TestApp", "TestTitle") },
            new FakeElevationChecker(),
            pill,
            sound,
            config,
            telemetry,
            time);

        controller.SetBackend(nextBackend);

        controller.Start();

        Assert.Equal(0, initialBackend.TurnCount);
        Assert.Equal(1, nextBackend.TurnCount);
    }
}
