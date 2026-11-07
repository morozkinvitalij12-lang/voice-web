import re
import webbrowser
from urllib.parse import urlencode


SITE_KEYWORDS: dict[str, tuple[str, ...]] = {
    "https://www.youtube.com": ("ютуб", "ютюб", "youtube", "you tube"),
    "https://www.google.com": ("гугл", "google"),
    "https://vk.com": ("вк", "vk", "вконтакте", "в контакте"),
    "https://web.telegram.org": ("телеграм", "телеграмм", "telegram"),
    "https://ru.wikipedia.org": ("википедия", "wikipedia"),
    "https://github.com": ("гитхаб", "github", "git hub"),
    "https://stackoverflow.com": ("стак оверфлоу", "stackoverflow", "stack overflow"),
    "https://www.reddit.com": ("реддит", "reddit"),
    "https://www.twitch.tv": ("твич", "twitch"),
    "https://www.ozon.ru": ("озон", "ozon"),
    "https://www.wildberries.ru": ("вайлдберриз", "wildberries"),
    "https://www.avito.ru": ("авито", "avito"),
    "https://habr.com": ("хабр", "habr"),
}


def normalize_text(text: str) -> str:
    return " ".join(text.lower().split())


def resolve_url(text: str) -> str:
    normalized = normalize_text(text)
    if not normalized:
        raise ValueError("Распознанный текст пуст.")

    matches: list[tuple[int, int, str]] = []
    for url, keywords in SITE_KEYWORDS.items():
        for keyword in keywords:
            # Границы слов не дают «вк» сработать внутри слова «включи».
            pattern = r"(?<!\w)" + re.escape(keyword) + r"(?!\w)"
            match = re.search(pattern, normalized)
            if match:
                matches.append((match.start(), -len(keyword), url))

    if matches:
        return min(matches)[2]
    return "https://www.google.com/search?" + urlencode({"q": normalized})


def open_site(text: str) -> str:
    url = resolve_url(text)
    if not webbrowser.open(url, new=2):
        raise RuntimeError("Не удалось открыть браузер по умолчанию.")
    return url
