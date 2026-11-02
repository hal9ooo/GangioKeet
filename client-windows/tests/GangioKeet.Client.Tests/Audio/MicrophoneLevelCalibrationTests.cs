using System.IO;
using GangioKeet.Client.Audio;
using Xunit;

namespace GangioKeet.Client.Tests.Audio;

public sealed class MicrophoneLevelCalibrationTests
{
    [Fact]
    public void Normalize_UsesTheCalibratedReference()
    {
        MicrophoneLevelCalibration calibration = new(reference: 0.2);

        Assert.Equal(0.5, calibration.Normalize(0.1), 6);
        Assert.Equal(1.0, calibration.Normalize(0.8), 6);
        Assert.Equal(0, calibration.Normalize(0));
        Assert.Equal(0, calibration.Normalize(double.NaN));
    }

    [Fact]
    public void Observe_CalibratesToTheLastRecording()
    {
        MicrophoneLevelCalibration calibration = new(reference: 0.3);

        calibration.Observe(0.1);

        Assert.Equal(0.16, calibration.Reference, 6);
        Assert.Equal(0.5, calibration.Normalize(0.08), 6);
    }

    [Fact]
    public void Observe_IgnoresSilenceAndNoise()
    {
        MicrophoneLevelCalibration calibration = new(reference: 0.3);

        calibration.Observe(0);
        calibration.Observe(0.005);
        calibration.Observe(double.NaN);

        Assert.Equal(0.3, calibration.Reference, 6);
    }

    [Fact]
    public void Reference_IsClampedToAUsableRange()
    {
        Assert.Equal(MicrophoneLevelCalibration.MinReference,
            new MicrophoneLevelCalibration(0.0001).Reference, 6);
        Assert.Equal(MicrophoneLevelCalibration.MaxReference,
            new MicrophoneLevelCalibration(1.0).Reference, 6);

        MicrophoneLevelCalibration calibration = new(reference: 0.3);
        calibration.Observe(1.0);
        Assert.InRange(calibration.Reference, 0.3, MicrophoneLevelCalibration.MaxReference);
    }

    [Fact]
    public void Store_RoundTripsTheReference()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "gangio-cal-" + Guid.NewGuid().ToString("N"));

        try
        {
            MicrophoneCalibrationStore store = new(root);
            Assert.Null(store.Load());

            store.Save(0.234);
            Assert.Equal(0.234, store.Load()!.Value, 6);

            store.Save(0.1);
            Assert.Equal(0.1, store.Load()!.Value, 6);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
