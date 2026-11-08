using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // A config file, not HTML: keep "?" and "&" in URLs readable instead of &.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <c>config/config.json</c>, or writes the defaults there on first start.
    /// Returns null when the radio must stay off: file just created, or not valid JSON.
    /// A file that lacks settings added by a later version gets them, with their defaults. Otherwise the
    /// file is left alone, so the user's layout and comments survive.
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

        RadioConfig config;
        JsonNode file;
        try
        {
            var text = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<RadioConfig>(text, Options) ?? new RadioConfig();
            file = JsonNode.Parse(text, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            logger.Error($"[Radio Norvinsk] {path} is not valid JSON, the radio stays off: {ex.Message}");
            return null;
        }

        if (file is JsonObject settings)
        {
            AddMissingSettings(path, settings, logger);
        }

        return config;
    }

    // Rewriting drops the file's comments, so it only happens when there is something to add.
    private static void AddMissingSettings(string path, JsonObject settings, ISptLogger<RadioNorvinskMod> logger)
    {
        var added = new List<string>();
        var defaults = JsonSerializer.SerializeToNode(new RadioConfig(), Options).AsObject();
        var completed = Complete(defaults, settings, "", added);
        if (added.Count == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, completed.ToJsonString(Options));
            logger.Info($"[Radio Norvinsk] Added new settings to {path}, with their defaults: {string.Join(", ", added)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth keeping the radio off: the missing settings already run with their defaults.
            logger.Warning($"[Radio Norvinsk] Could not add new settings to {path} ({ex.Message}). They run with their defaults.");
        }
    }

    /// <summary>
    /// The defaults' layout with the user's values: a setting the file lacks comes in with its default,
    /// and keys the mod doesn't know are kept, at the end.
    /// </summary>
    private static JsonObject Complete(JsonObject defaults, JsonObject user, string prefix, List<string> added)
    {
        var result = new JsonObject();
        var matched = new HashSet<string>();

        foreach (var (key, defaultValue) in defaults)
        {
            // Reading is case-insensitive, so "WebhookUrl" counts as "webhookUrl".
            var userKey = user.Select(property => property.Key)
                .FirstOrDefault(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase));
            if (userKey == null)
            {
                result[key] = defaultValue?.DeepClone();
                added.Add(prefix + key);
                continue;
            }

            matched.Add(userKey);
            result[userKey] = defaultValue is JsonObject defaultObject && user[userKey] is JsonObject userObject
                ? Complete(defaultObject, userObject, prefix + key + ".", added)
                : user[userKey]?.DeepClone();
        }

        foreach (var (key, value) in user)
        {
            if (!matched.Contains(key))
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }
}
