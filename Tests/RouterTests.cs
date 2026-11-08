using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class RouterTests
{
    [Theory]
    [InlineData("Открой ЮТУБ!", "https://www.youtube.com")]
    [InlineData("open youtube please", "https://www.youtube.com")]
    [InlineData("you tube", "https://www.youtube.com")]
    [InlineData("ютюб", "https://www.youtube.com")]
    [InlineData("гугл", "https://www.google.com")]
    [InlineData("GOOGLE", "https://www.google.com")]
    [InlineData("вк", "https://vk.com")]
    [InlineData("VK", "https://vk.com")]
    [InlineData("в контакте", "https://vk.com")]
    [InlineData("вконтакте", "https://vk.com")]
    [InlineData("телеграм", "https://web.telegram.org")]
    [InlineData("telegram", "https://web.telegram.org")]
    [InlineData("телеграмм", "https://web.telegram.org")]
    [InlineData("Википедия", "https://ru.wikipedia.org")]
    [InlineData("wikipedia", "https://ru.wikipedia.org")]
    [InlineData("github", "https://github.com")]
    [InlineData("git hub", "https://github.com")]
    [InlineData("гитхаб", "https://github.com")]
    [InlineData("stack overflow", "https://stackoverflow.com")]
    [InlineData("stackoverflow", "https://stackoverflow.com")]
    [InlineData("стак оверфлоу", "https://stackoverflow.com")]
    [InlineData("реддит", "https://www.reddit.com")]
    [InlineData("reddit", "https://www.reddit.com")]
    [InlineData("твич", "https://www.twitch.tv")]
    [InlineData("twitch", "https://www.twitch.tv")]
    [InlineData("озон", "https://www.ozon.ru")]
    [InlineData("ozon", "https://www.ozon.ru")]
    [InlineData("wildberries", "https://www.wildberries.ru")]
    [InlineData("вайлдберриз", "https://www.wildberries.ru")]
    [InlineData("авито", "https://www.avito.ru")]
    [InlineData("avito", "https://www.avito.ru")]
    [InlineData("хабр", "https://habr.com")]
    [InlineData("habr", "https://habr.com")]
    public void RecognizesAliases(string text, string expected) => Assert.Equal(expected, SiteRouter.Resolve(text));

    [Theory]
    [InlineData("включи музыку")]
    [InlineData("myyoutubechannel")]
    [InlineData("github123")]
    [InlineData("_vk_")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.invalid/?q=a&secret=b")]
    [InlineData("погода завтра & дождь #1 + 20%")]
    [InlineData("\"; shutdown /s & calc.exe")]
    public void UnknownTextRemainsEncodedGoogleQuery(string text)
    {
        var url = SiteRouter.Resolve(text);
        var normalized = text.ToLowerInvariant();
        Assert.Equal("https://www.google.com/search?q=" + Uri.EscapeDataString(normalized), url);
        Assert.Equal("www.google.com", new Uri(url).Host);
    }

    [Fact]
    public void CollapsesWhitespace() => Assert.Equal(
        "https://www.google.com/search?q=" + Uri.EscapeDataString("погода завтра"),
        SiteRouter.Resolve("  ПОГОДА\t \nЗАВТРА  "));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t\r\n")]
    public void RejectsEmptySpeech(string text) => Assert.Throws<ArgumentException>(() => SiteRouter.Resolve(text));

    [Theory]
    [InlineData("reddit then youtube", "https://www.reddit.com")]
    [InlineData("ютуб и вк", "https://www.youtube.com")]
    [InlineData("гугл github", "https://www.google.com")]
    public void PicksFirstMention(string text, string expected) => Assert.Equal(expected, SiteRouter.Resolve(text));
}
