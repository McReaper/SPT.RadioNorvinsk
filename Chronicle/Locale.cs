using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Chronicle;

/// <summary>
/// The radio's lines, read from <c>locales/&lt;language&gt;.json</c> over <c>locales/en.json</c>:
/// a key the chosen language lacks is said in English.
/// </summary>
internal sealed class Locale
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, string> _formats;
    private readonly Dictionary<string, List<string>> _lines;
    private readonly Dictionary<string, int> _lastPick = new();
    private readonly Random _random = new();
    private readonly object _lock = new();

    private Locale(Dictionary<string, string> formats, Dictionary<string, List<string>> lines)
    {
        _formats = formats;
        _lines = lines;
    }

    /// <summary>Returns null when <c>locales/en.json</c>, the base every language falls back to, can't be read.</summary>
    public static Locale Load(string modFolder, string language, ISptLogger<RadioNorvinskMod> logger)
    {
        var folder = Path.Combine(modFolder, "locales");
        var english = Read(Path.Combine(folder, "en.json"), logger);
        if (english == null)
        {
            return null;
        }

        var formats = new Dictionary<string, string>(english.Formats, StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, List<string>>(english.Lines, StringComparer.OrdinalIgnoreCase);

        if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = Read(Path.Combine(folder, $"{language}.json"), logger);
            if (chosen == null)
            {
                logger.Warning($"[Radio Norvinsk] No usable locales/{language}.json, speaking English.");
            }
            else
            {
                foreach (var (key, value) in chosen.Formats)
                {
                    formats[key] = value;
                }

                foreach (var (key, variants) in chosen.Lines)
                {
                    if (variants is { Count: > 0 })
                    {
                        lines[key] = variants;
                    }
                }
            }
        }

        return new Locale(formats, lines);
    }

    /// <summary>
    /// One variant of <paramref name="key"/>, never the same one twice in a row, with each
    /// <c>{name}</c> placeholder replaced. Null when no variant exists for the key.
    /// </summary>
    public string Line(string key, params (string Name, string Value)[] values)
    {
        if (!_lines.TryGetValue(key, out var variants) || variants is not { Count: > 0 })
        {
            return null;
        }

        string variant;
        lock (_lock)
        {
            var last = _lastPick.GetValueOrDefault(key, -1);
            var index = _random.Next(variants.Count);
            if (index == last && variants.Count > 1)
            {
                index = (index + 1 + _random.Next(variants.Count - 1)) % variants.Count;
            }

            _lastPick[key] = index;
            variant = variants[index];
        }

        return Fill(variant, values);
    }

    /// <summary>A format string from <c>formats</c>, filled like a line. Falls back to the key itself.</summary>
    public string Format(string key, params (string Name, string Value)[] values)
    {
        return Fill(_formats.GetValueOrDefault(key, key), values);
    }

    private static string Fill(string text, (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value);
        }

        return text;
    }

    private static LocaleFile Read(string path, ISptLogger<RadioNorvinskMod> logger)
    {
        if (!File.Exists(path))
        {
            logger.Error($"[Radio Norvinsk] Missing {path}.");
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<LocaleFile>(File.ReadAllText(path), Options) ?? new LocaleFile();
            file.Formats ??= new();
            file.Lines ??= new();
            return file;
        }
        catch (JsonException ex)
        {
            logger.Error($"[Radio Norvinsk] {path} is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private sealed class LocaleFile
    {
        public Dictionary<string, string> Formats { get; set; } = new();
        public Dictionary<string, List<string>> Lines { get; set; } = new();
    }
}
