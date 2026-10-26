using GangioKeet.Client.Paste;
using Xunit;

namespace GangioKeet.Client.Tests.Paste;

[Trait("Category", "Integration")]
public sealed class Win32ClipboardTests
{
    [SkippableFact]
    public void RoundtripText_ReturnsSameSequence()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        using var dispatcher = new StaPasteDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        uint sequence = dispatcher.InvokeAsync(() =>
        {
            var clipboard = new Win32ClipboardService();
            ClipboardSnapshot backup = clipboard.Backup();
            uint seq = clipboard.SetText("test roundtrip");
            Assert.True(clipboard.TryRestore(backup, seq));
            return seq;
        }, cts.Token).GetAwaiter().GetResult();

        Assert.NotEqual(0u, sequence);
    }

    [SkippableFact]
    public void ExternalCopy_PreventsRestore()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        using var dispatcher = new StaPasteDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        bool restoreResult = dispatcher.InvokeAsync(() =>
        {
            var clipboard = new Win32ClipboardService();
            ClipboardSnapshot backup = clipboard.Backup();
            uint seq = clipboard.SetText("originale");

            // Simula copia esterna: scrive altro testo
            clipboard.SetText("esterno");

            // Il ripristino dovrebbe fallire perché il sequence number è cambiato
            return clipboard.TryRestore(backup, seq);
        }, cts.Token).GetAwaiter().GetResult();

        Assert.False(restoreResult);
    }

    [SkippableFact]
    public void BackupAndRestoreTextAndDib()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        using var dispatcher = new StaPasteDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        dispatcher.InvokeAsync<object?>(() =>
        {
            var clipboard = new Win32ClipboardService();
            ClipboardSnapshot backup = clipboard.Backup();
            uint seq = clipboard.SetText("test con dib");
            Assert.True(clipboard.TryRestore(backup, seq));
            return null;
        }, cts.Token).GetAwaiter().GetResult();
    }
}
