using GangioKeet.Client.Platform;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class LocalSettingsInfoTests
{
    [Fact]
    public void FormatsTheBuiltInLightCleanerWithANiceLabel() =>
        Assert.Equal("parakeet_v3 + Pulizia leggera",
            LocalSettingsInfo.FormatPair("parakeet_v3", LightTextCleaner.Id));
}
