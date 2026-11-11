using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class LanguageTests
{
    [Theory]
    [InlineData("ru", 0.95f, "ru")]
    [InlineData("ru", 0.40f, "ru")]
    [InlineData("en", 0.95f, "en")]
    [InlineData("en", 0.75f, "en")]
    [InlineData("en", 0.74f, "ru")]
    [InlineData("en", 0.10f, "ru")]
    [InlineData(null, 0f, "ru")]
    [InlineData("de", 0.99f, "ru")]
    public void SelectsRussianUnlessEnglishIsConfident(string? detected, float probability, string expected)
        => Assert.Equal(expected, SpeechRecognizer.SelectLanguage(detected, probability));
}
