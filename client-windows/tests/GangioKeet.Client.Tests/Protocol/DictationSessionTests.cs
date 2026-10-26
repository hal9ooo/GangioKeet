using GangioKeet.Client.Protocol;
using GangioKeet.Client.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class DictationSessionTests
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbortGrace = TimeSpan.FromMilliseconds(250);
    private static readonly Uri SessionUri = new("ws://fake-host.local/ws/dictate");

    private static StartMessage NewStart() => new("dictation-test", "Terminal Window", "code");

    private static ResultFrame NewResultFrame() => new(new ResultMessage(
        "raw speech",
        "Final speech.",
        FastPath: false,
        new DictationMetrics(SttMs: 10.0, LlmMs: 0.0, TotalMs: 10.0),
        []));

    private static byte[] NewPcm(int length, byte firstByte = 0)
    {
        byte[] pcm = new byte[length];
        pcm[0] = firstByte;
        for (int i = 1; i < length; i++)
        {
            pcm[i] = (byte)(length + i);
        }
        return pcm;
    }

    private static DictationSession NewSession(ScriptedTransport transport, FakeTimeProvider clock)
    {
        return new DictationSession(transport, clock, ResponseTimeout);
    }

    [Fact]
    public async Task ErrorDuringRecording_CompletesImmediately()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), "secret-token", ConnectTimeout, CancellationToken.None);

        Assert.True(connect.Connected);
        Assert.Null(connect.Failure);
        Assert.Equal(SessionUri, transport.ConnectedUri);
        Assert.Equal("secret-token", transport.ConnectedToken);

        transport.Deliver(new ErrorFrame("model_fallback", "Transcription failed.", Retryable: true));

        DictationOutcome outcome = await session.Completion;

        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Error);
        Assert.Equal("model_fallback", outcome.Error.Code);
        Assert.Equal("Transcription failed.", outcome.Error.Message);
        Assert.True(outcome.Error.Retryable);
        Assert.True(session.Completion.IsCompleted);

        DictationOutcome finishOutcome = await session.FinishAsync(CancellationToken.None);
        Assert.Equal(DictationStatus.Error, finishOutcome.Status);
        Assert.Equal(1, transport.Order.Count(entry => entry == "text:" + DictationSession.StopMessageJson));
        Assert.Equal(0, transport.AbortCount);
    }

    [Fact]
    public async Task Close1009DuringRecording_NoResultRequired()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);

        Assert.True(connect.Connected);

        transport.DeliverException(new TransportClosedException(closeCode: 1009, closeReason: "payload_too_large"));

        DictationOutcome outcome = await session.Completion;

        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Error);
        Assert.Equal("closed", outcome.Error.Code);
        Assert.Contains("1009", outcome.Error.Message, StringComparison.Ordinal);
        Assert.Contains("payload_too_large", outcome.Error.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Error.HttpStatus);
        Assert.True(session.Completion.IsCompleted);

        await session.FinishAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectFailure_ReturnedImmediately()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);
        transport.ConnectException = new TransportConnectException(
            new ConnectFailure("forbidden", "WebSocket handshake rejected with HTTP 403.", 403));

        SessionConnectOutcome outcome = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), "secret-token", ConnectTimeout, CancellationToken.None);

        Assert.False(outcome.Connected);
        Assert.NotNull(outcome.Failure);
        Assert.Equal("forbidden", outcome.Failure.Code);
        Assert.Equal(403, outcome.Failure.HttpStatus);

        DictationOutcome terminal = await session.Completion;
        Assert.Equal(DictationStatus.Error, terminal.Status);
        Assert.NotNull(terminal.Error);
        Assert.Equal("forbidden", terminal.Error.Code);
        Assert.Equal(403, terminal.Error.HttpStatus);
        Assert.True(session.Completion.IsCompleted);

        Assert.Throws<InvalidOperationException>(() => session.TryEnqueueAudio(NewPcm(4, 7)));

        DictationOutcome finishOutcome = await session.FinishAsync(CancellationToken.None);
        Assert.Equal(DictationStatus.Error, finishOutcome.Status);
        Assert.Equal(["connect"], transport.Order);
    }

    [Fact]
    public async Task OnlyOneSendAndOneReceiveInFlight()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        transport.DeliverAfterStop.Add(NewResultFrame());

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(session.TryEnqueueAudio(NewPcm(length: 32, firstByte: (byte)i)));
        }

        DictationOutcome outcome = await session.FinishAsync(CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Equal("Final speech.", outcome.Result!.FinalText);
        Assert.Equal(1, transport.MaxConcurrentTextSends);
        Assert.Equal(1, transport.MaxConcurrentBinarySends);
        Assert.Equal(1, transport.MaxConcurrentReceives);
        Assert.Equal(4, transport.SentAudio.Count);
    }

    [Fact]
    public async Task QueuedAudio_PrecedesStop()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        transport.DeliverAfterStop.Add(NewResultFrame());
        string startJson = ProtocolJson.SerializeStart(NewStart());
        byte[] shared = new byte[16];

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        for (int i = 0; i < 20; i++)
        {
            shared[0] = (byte)i;
            Assert.True(session.TryEnqueueAudio(shared));
        }
        // Il caller riusa il buffer: le copie accodate restano reattive al contenuto originale.
        shared[0] = 0xAA;

        DictationOutcome outcome = await session.FinishAsync(CancellationToken.None);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Equal(20, transport.SentAudio.Count);
        Assert.Equal(1, transport.Order.Count(entry => entry == "text:" + DictationSession.StopMessageJson));
        Assert.Equal(2 + 20, transport.Order.IndexOf("text:" + DictationSession.StopMessageJson));
        Assert.Equal("text:" + startJson, transport.Order[1]);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("binary:16", transport.Order[2 + i]);
            Assert.Equal((byte)i, transport.SentAudio[i][0]);
        }
        // Nessuna copia contiene il valore post-riuso: la sessione non ha mai retenuto il buffer.
        Assert.All(transport.SentAudio, chunk => Assert.NotEqual((byte)0xAA, chunk[0]));
    }

    [Fact]
    public async Task RepeatedFinish_SendsOneStop()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        transport.DeliverAfterStop.Add(NewResultFrame());

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);
        Assert.True(session.TryEnqueueAudio(NewPcm(8)));

        DictationOutcome first = await session.FinishAsync(CancellationToken.None);
        DictationOutcome second = await session.FinishAsync(CancellationToken.None);

        Assert.Equal(DictationStatus.Result, first.Status);
        Assert.Equal(DictationStatus.Result, second.Status);
        Assert.Equal("Final speech.", first.Result!.FinalText);
        Assert.Equal(1, transport.Order.Count(entry => entry == "text:" + DictationSession.StopMessageJson));
    }

    [Fact]
    public async Task QueueFull_FailsWithoutDroppingAudioSilently()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        transport.HangNextSendBinary();
        Assert.True(session.TryEnqueueAudio(NewPcm(16, 1)));
        await transport.WhenBinarySendHangs;

        int accepted = 2;
        while (session.TryEnqueueAudio(NewPcm(16, (byte)accepted)))
        {
            accepted++;
        }
        Assert.Equal(22, accepted);
        Assert.False(session.TryEnqueueAudio(NewPcm(16, 255)));
        Assert.Empty(transport.SentAudio);

        transport.ReleasePendingSend();

        using CancellationTokenSource finishCts = new();
        Task finishTask = session.FinishAsync(finishCts.Token);
        await transport.WhenStopSent;
        finishCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishTask);

        Assert.Equal(21, transport.SentAudio.Count);
        for (int i = 0; i < 21; i++)
        {
            Assert.Equal("binary:16", transport.Order[2 + i]);
            Assert.Equal((byte)(i + 1), transport.SentAudio[i][0]);
        }
        Assert.Equal("text:" + DictationSession.StopMessageJson, transport.Order[^1]);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Timeout_ClosesTransportAndIgnoresLateResult()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);
        Assert.True(session.TryEnqueueAudio(NewPcm(16)));

        using CancellationTokenSource finishCts = new();
        Task<DictationOutcome> finishTask = session.FinishAsync(finishCts.Token);

        Task<DictationOutcome> completion = session.Completion;
        await transport.WhenStopSent;

        clock.Advance(ResponseTimeout);

        DictationOutcome outcome = await completion;
        DictationOutcome finishOutcome = await finishTask;
        Assert.Equal(DictationStatus.Error, finishOutcome.Status);

        Assert.Equal(DictationStatus.Error, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Error);
        Assert.Equal("timeout", outcome.Error.Code);
        Assert.True(outcome.Error.Retryable);
        Assert.Equal(1, transport.AbortCount);

        transport.Deliver(NewResultFrame());
        DictationOutcome late = await session.Completion;
        Assert.Equal(DictationStatus.Error, late.Status);
        Assert.Equal("timeout", late.Error!.Code);
    }

    [Fact]
    public async Task AbortRacingResult_NeverPublishesSuccess()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        Task abortTask = session.AbortAsync(CancellationToken.None);

        DictationOutcome outcome = await session.Completion;

        Assert.Equal(DictationStatus.Aborted, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Error);
        Assert.Equal("aborted", outcome.Error.Code);
        Assert.False(outcome.Error.Retryable);

        transport.Deliver(NewResultFrame());
        await abortTask;

        DictationOutcome after = await session.Completion;
        Assert.Equal(DictationStatus.Aborted, after.Status);
        Assert.Equal(1, transport.AbortCount);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task Abort_AbortsSocketWhenGracefulCloseTimesOut()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        await using DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        transport.HangNextClose();
        Task abortTask = session.AbortAsync(CancellationToken.None);
        clock.Advance(AbortGrace);

        await abortTask;

        DictationOutcome outcome = await session.Completion;
        Assert.Equal(DictationStatus.Aborted, outcome.Status);
        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.AbortCount);
        Assert.Equal("connect", transport.Order[0]);
        Assert.Equal("close", transport.Order[^2]);
        Assert.Equal("abort", transport.Order[^1]);
    }

    [Fact]
    public async Task Dispose_CancelsAndJoinsPumpsWithoutHanging()
    {
        ScriptedTransport transport = new();
        FakeTimeProvider clock = new();
        DictationSession session = NewSession(transport, clock);

        SessionConnectOutcome connect = await session.ConnectAndStartAsync(
            SessionUri, NewStart(), null, ConnectTimeout, CancellationToken.None);
        Assert.True(connect.Connected);

        // Receiver appeso su Receive (nessun frame), sender appeso su WaitToReadAsync:
        // va tutto cancellato e atteso in DisposeAsync, senza hang.
        Task<DictationOutcome> completion = session.Completion;
        await session.DisposeAsync();

        DictationOutcome outcome = await completion;
        Assert.Equal(DictationStatus.Aborted, outcome.Status);
        Assert.Equal(1, transport.AbortCount);

        await session.DisposeAsync();
    }
}
