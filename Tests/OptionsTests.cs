using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void DefaultsAreSmallAndAuto() => Assert.Equal(new LaunchOptions("small", "auto", false, false), LaunchOptions.Parse([]));

    [Theory]
    [InlineData("base", "cpu")]
    [InlineData("base", "cuda")]
    [InlineData("small", "auto")]
    [InlineData("small", "cpu")]
    public void AcceptsModelAndDevice(string model, string device) => Assert.Equal(
        new LaunchOptions(model, device, false, false), LaunchOptions.Parse(["--device", device, "--model", model]));

    [Theory]
    [InlineData("--install-startup", true, false)]
    [InlineData("--remove-startup", false, true)]
    public void StartupIsExplicit(string argument, bool install, bool remove) => Assert.Equal(
        new LaunchOptions("small", "auto", install, remove), LaunchOptions.Parse([argument]));

    [Theory]
    [InlineData("--model")]
    [InlineData("--device")]
    [InlineData("--unknown")]
    [InlineData("--install-startup --remove-startup")]
    [InlineData("--model base.en")]
    [InlineData("--device vulkan")]
    [InlineData("--model base&calc")]
    [InlineData("--device cuda;calc")]
    public void RejectsInvalidArguments(string arguments) => Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(arguments.Split(' ')));
}
