using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData(0x2F, 0x40, 0x2F, 0x40)]
    [InlineData(0x2F, 0x40, 0x40, 0x2F)]
    [InlineData(0x40, 0x2F, 0x2F, 0x40)]
    [InlineData(0x40, 0x2F, 0x40, 0x2F)]
    public void StartsAndStopsOnlyWhenBothKeysAgree(uint firstDown, uint secondDown, uint firstUp, uint secondUp)
    {
        var starts = 0;
        var stops = 0;
        var keys = new PushToTalk(() => starts++, () => stops++);
        keys.Handle(firstDown, true);
        Assert.Equal(0, starts);
        keys.Handle(secondDown, true);
        Assert.Equal(1, starts);
        keys.Handle(firstUp, false);
        Assert.Equal(0, stops);
        keys.Handle(secondUp, false);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void IgnoresRepeatAndOtherKeys()
    {
        var starts = 0;
        var stops = 0;
        var keys = new PushToTalk(() => starts++, () => stops++);
        keys.Handle(0x1E, true);
        keys.Handle(0x2F, true);
        keys.Handle(0x2F, true);
        Assert.Equal(0, starts);
        keys.Handle(0x40, true);
        keys.Handle(0x40, true);
        keys.Handle(0x1E, false);
        keys.Handle(0x2F, false);
        keys.Handle(0x2F, true);
        Assert.Equal(1, starts);
        Assert.Equal(0, stops);
        keys.Handle(0x40, false);
        Assert.Equal(0, stops);
        keys.Handle(0x2F, false);
        keys.Handle(0x2F, false);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void MultipleSessionsAreIndependent()
    {
        var events = new List<string>();
        var keys = new PushToTalk(() => events.Add("start"), () => events.Add("stop"));
        for (var i = 0; i < 3; i++)
        {
            keys.Handle(0x2F, true);
            keys.Handle(0x40, true);
            keys.Handle(0x40, false);
            keys.Handle(0x2F, false);
        }
        Assert.Equal(new[] { "start", "stop", "start", "stop", "start", "stop" }, events);
    }

    [Fact]
    public void StrayReleasesDoNotStartOrStop()
    {
        var events = 0;
        var keys = new PushToTalk(() => events++, () => events++);
        keys.Handle(0x2F, false);
        keys.Handle(0x40, false);
        keys.Handle(0x2F, true);
        keys.Handle(0x2F, false);
        Assert.Equal(0, events);
    }
}
