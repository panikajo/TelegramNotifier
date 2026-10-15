using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TelegramNotifier;

internal sealed class TranslationCatalog
{
    public string ServerStatus { get; set; } =
        "<b>{HOSTNAME}</b>\n<b>Map: </b>{MAP}\n<b>Players: </b>{CURRENT}/{MAX}";

    public string PlayerConnected { get; set; } =
        "<b>{NAME}</b>\nconnected\n<code>{STEAMID}</code>\n<a href=\"https://steamcommunity.com/profiles/{STEAMID}\">Profile</a>";

    public static TranslationCatalog Load(string pluginPath, string language, ILogger logger)
    {
        var languageCode = SanitizeLanguageCode(language);
        var languageDirectory = Path.Combine(pluginPath, "resources", "language");
        var requestedPath = Path.Combine(languageDirectory, $"{languageCode}.jsonc");
        var fallbackPath = Path.Combine(languageDirectory, "en.jsonc");

        var loaded = TryLoad(requestedPath, logger);
        if (loaded is not null)
            return loaded;

        if (!requestedPath.Equals(fallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "[TelegramNotifier] Translation '{Language}' was not found. Falling back to English.",
                languageCode);
            loaded = TryLoad(fallbackPath, logger);
        }

        return loaded ?? new TranslationCatalog();
    }

    private static TranslationCatalog? TryLoad(string path, ILogger logger)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            return JsonSerializer.Deserialize<TranslationCatalog>(File.ReadAllText(path), options);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TelegramNotifier] Failed to load translation file {Path}", path);
            return null;
        }
    }

    private static string SanitizeLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var safe = new string(language
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(safe) ? "en" : safe;
    }
}