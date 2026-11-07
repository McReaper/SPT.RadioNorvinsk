using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Chronicle;

/// <summary>
/// The radio's words, read from <c>locales/&lt;language&gt;.json</c> over <c>locales/en.json</c>:
/// anything the chosen language lacks is said in English.
/// </summary>
internal sealed class Locale
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Dictionary<string, string>> _sections;
    private readonly Dictionary<string, List<string>> _lines;
    private readonly Dictionary<string, int> _lastPick = new();
    private readonly Random _random = new();
    private readonly object _lock = new();

    private Locale(Dictionary<string, Dictionary<string, string>> sections, Dictionary<string, List<string>> lines)
    {
        _sections = sections;
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

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Merge(english, sections, lines);

        if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = Read(Path.Combine(folder, $"{language}.json"), logger);
            if (chosen == null)
            {
                logger.Warning($"[Radio Norvinsk] No usable locales/{language}.json, speaking English.");
            }
            else
            {
                Merge(chosen, sections, lines);
            }
        }

        return new Locale(sections, lines);
    }

    /// <summary>
    /// One variant of the first key in <paramref name="keys"/> that has any, never the same variant twice
    /// in a row, with each <c>{name}</c> placeholder replaced. Null when none of the keys has a line.
    /// </summary>
    public string Line(IEnumerable<string> keys, params (string Name, string Value)[] values)
    {
        foreach (var key in keys)
        {
            if (!_lines.TryGetValue(key, out var variants) || variants.Count == 0)
            {
                continue;
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

        return null;
    }

    /// <summary>An entry of a section (<c>maps</c>, <c>roles</c>, <c>labels</c>...), or null.</summary>
    public string Text(string section, string key)
    {
        return key != null && _sections.TryGetValue(section, out var texts) && texts.TryGetValue(key, out var text)
            ? text
            : null;
    }

    /// <summary>An entry of <c>formats</c>, filled like a line. Falls back to the key itself.</summary>
    public string Format(string key, params (string Name, string Value)[] values)
    {
        return Fill(Text("formats", key) ?? key, values);
    }

    private static string Fill(string text, (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value ?? "");
        }

        return text;
    }

    private static void Merge(LocaleFile file, Dictionary<string, Dictionary<string, string>> sections, Dictionary<string, List<string>> lines)
    {
        foreach (var (name, texts) in file.Sections())
        {
            if (texts == null)
            {
                continue;
            }

            if (!sections.TryGetValue(name, out var merged))
            {
                sections[name] = merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var (key, value) in texts)
            {
                merged[key] = value;
            }
        }

        foreach (var (key, variants) in file.Lines ?? new())
        {
            if (variants is { Count: > 0 })
            {
                lines[key] = variants;
            }
        }
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
            return JsonSerializer.Deserialize<LocaleFile>(File.ReadAllText(path), Options) ?? new LocaleFile();
        }
        catch (JsonException ex)
        {
            logger.Error($"[Radio Norvinsk] {path} is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private sealed class LocaleFile
    {
        public Dictionary<string, string> Formats { get; set; }
        public Dictionary<string, string> Labels { get; set; }
        public Dictionary<string, string> Maps { get; set; }
        public Dictionary<string, string> MapsAt { get; set; }
        public Dictionary<string, string> Roles { get; set; }
        public Dictionary<string, string> BodyParts { get; set; }
        public Dictionary<string, string> HeadSegments { get; set; }
        public Dictionary<string, string> Causes { get; set; }
        public Dictionary<string, List<string>> Lines { get; set; }

        public IEnumerable<(string Name, Dictionary<string, string> Texts)> Sections()
        {
            yield return ("formats", Formats);
            yield return ("labels", Labels);
            yield return ("maps", Maps);
            yield return ("mapsAt", MapsAt);
            yield return ("roles", Roles);
            yield return ("bodyParts", BodyParts);
            yield return ("headSegments", HeadSegments);
            yield return ("causes", Causes);
        }
    }
}
