using System.IO;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Config;

internal static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Reads <c>config/config.json</c>, or writes the defaults there on first start.
    /// Returns null when the radio must stay off: file just created, or not valid JSON.
    /// An existing file is never rewritten, so the user's layout and comments survive.
    /// </summary>
    public static RadioConfig Load(string modFolder, ISptLogger<RadioNorvinskMod> logger)
    {
        var path = Path.Combine(modFolder, "config", "config.json");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(new RadioConfig(), Options));
            logger.Warning($"[Radio Norvinsk] Created {path}. Paste a Discord webhook URL into it, then restart the server.");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RadioConfig>(File.ReadAllText(path), Options) ?? new RadioConfig();
        }
        catch (JsonException ex)
        {
            logger.Error($"[Radio Norvinsk] {path} is not valid JSON, the radio stays off: {ex.Message}");
            return null;
        }
    }
}
