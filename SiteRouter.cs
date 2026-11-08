using System.Text.RegularExpressions;

namespace VoiceSiteLauncher;

internal static class SiteRouter
{
    private static readonly Dictionary<string, string[]> Sites = new()
    {
        ["https://www.youtube.com"] = ["ютуб", "ютюб", "youtube", "you tube"],
        ["https://www.google.com"] = ["гугл", "google"],
        ["https://vk.com"] = ["вк", "vk", "вконтакте", "в контакте"],
        ["https://web.telegram.org"] = ["телеграм", "телеграмм", "telegram"],
        ["https://ru.wikipedia.org"] = ["википедия", "wikipedia"],
        ["https://github.com"] = ["гитхаб", "github", "git hub"],
        ["https://stackoverflow.com"] = ["стак оверфлоу", "stackoverflow", "stack overflow"],
        ["https://www.reddit.com"] = ["реддит", "reddit"],
        ["https://www.twitch.tv"] = ["твич", "twitch"],
        ["https://www.ozon.ru"] = ["озон", "ozon"],
        ["https://www.wildberries.ru"] = ["вайлдберриз", "wildberries"],
        ["https://www.avito.ru"] = ["авито", "avito"],
        ["https://habr.com"] = ["хабр", "habr"]
    };

    public static string Resolve(string text)
    {
        var normalized = Regex.Replace(text.ToLowerInvariant().Trim(), @"\s+", " ");
        if (normalized.Length == 0)
            throw new ArgumentException("Распознанный текст пуст.");
        var matches = new List<(int Position, int Length, string Url)>();
        foreach (var (url, aliases) in Sites)
        {
            foreach (var alias in aliases)
            {
                var match = Regex.Match(normalized, @"(?<!\w)" + Regex.Escape(alias) + @"(?!\w)");
                if (match.Success)
                    matches.Add((match.Index, -alias.Length, url));
            }
        }
        return matches.Count == 0
            ? "https://www.google.com/search?q=" + Uri.EscapeDataString(normalized)
            : matches.OrderBy(x => x.Position).ThenBy(x => x.Length).ThenBy(x => x.Url, StringComparer.Ordinal).First().Url;
    }
}
