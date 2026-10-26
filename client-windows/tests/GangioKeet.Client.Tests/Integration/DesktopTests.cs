using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Context;
using GangioKeet.Client.Hotkey;
using GangioKeet.Client.Paste;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Integration;

/// <summary>
/// Smoke test desktop: richiedono RUN_DESKTOP_TESTS=1 e un desktop Windows reale.
/// Non usano il server Mac.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DesktopTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1";

    [SkippableFact]
    public void ForegroundWindow_CanBeReadAndChecked()
    {
        Skip.IfNot(Enabled, "RUN_DESKTOP_TESTS != 1");

        var service = new Win32ForegroundWindowService();
        nint handle = service.CaptureHandle();
        Skip.IfNot(handle != 0, "nessuna finestra in primo piano");

        ForegroundWindowInfo info = service.Read(handle);
        Assert.Equal(handle, info.Handle);
        Assert.True(service.IsAlive(info));
    }

    [SkippableFact]
    public void Clipboard_RestoresTextAndSequenceProtectsExternalCopy()
    {
        Skip.IfNot(Enabled, "RUN_DESKTOP_TESTS != 1");

        using var dispatcher = new StaPasteDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        bool protectedCopy = dispatcher.InvokeAsync(() =>
        {
            var clipboard = new Win32ClipboardService();
            ClipboardSnapshot backup = clipboard.Backup();
            uint seq = clipboard.SetText("gangio-keet-desktop-test");

            // Nessuna copia esterna: il ripristino deve riuscire
            Assert.True(clipboard.TryRestore(backup, seq));

            // Copia esterna simulata: il ripristino deve fallire
            uint seq2 = clipboard.SetText("originale");
            clipboard.SetText("copia-esterna");
            return !clipboard.TryRestore(backup, seq2);
        }, cts.Token).GetAwaiter().GetResult();

        Assert.True(protectedCopy);
    }

    [SkippableFact]
    public void ToneGenerator_ProducesPlayableWav()
    {
        Skip.IfNot(Enabled, "RUN_DESKTOP_TESTS != 1");

        byte[] wav = SynthSoundPlayer.GenerateToneWav(880, 60);
        Assert.True(wav.Length > 44);

        using var player = new SynthSoundPlayer();
        player.Play(GangioKeet.Client.Controller.SoundKind.Start);
    }

    [SkippableFact]
    public async Task AudioDevices_EnumerateWithoutCrash()
    {
        Skip.IfNot(Enabled, "RUN_DESKTOP_TESTS != 1");

        IReadOnlyList<AudioDeviceInfo> devices = await new AudioDevices().ListAsync(CancellationToken.None);
        Assert.NotNull(devices);
    }

    [SkippableFact]
    public void InputHookBackend_InstallsAndDisposes()
    {
        Skip.IfNot(Enabled, "RUN_DESKTOP_TESTS != 1");

        using var backend = new Win32InputHookBackend();
        // Il backend non parte finché Run non è chiamato: il dispose è sicuro.
        Assert.NotNull(backend);
    }
}
