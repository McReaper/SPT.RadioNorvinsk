using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // A key's lines take turns by the week: each week says a third of them, never fewer than MinWeeklyLines, so that
    // the next week sounds new. A key with fewer than twice that says all of its lines every week.
    private const int RotationWeeks = 3;
    private const int MinWeeklyLines = 4;

    // The radio's day starts at 05:00, server time: a session past midnight still belongs to the evening it began in.
    // Its weeks start on Monday, at the same hour.
    private static readonly TimeSpan DayStart = TimeSpan.FromHours(5);

    private static readonly Dictionary<string, DayOfWeek> DayNames =
        Enum.GetValues<DayOfWeek>().ToDictionary(day => day.ToString(), StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, string>> _sections;
    private readonly Dictionary<string, List<string>> _lines;
    private readonly Dictionary<string, Dictionary<DayOfWeek, List<string>>> _dayLines;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<string, Bag> _bags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
    private readonly object _lock = new();

    private Locale(
        Dictionary<string, Dictionary<string, string>> sections,
        Dictionary<string, List<string>> lines,
        Dictionary<string, Dictionary<DayOfWeek, List<string>>> dayLines,
        Func<DateTime> clock)
    {
        _sections = sections;
        _lines = lines;
        _dayLines = dayLines;
        _clock = clock;
    }

    /// <summary>Returns null when <c>locales/en.json</c>, the base every language falls back to, can't be read.</summary>
    /// <param name="clock">The server's local time. Null: <see cref="DateTime.Now"/>.</param>
    public static Locale Load(string modFolder, string language, ISptLogger<RadioNorvinskMod> logger, Func<DateTime> clock = null)
    {
        var folder = Path.Combine(modFolder, "locales");
        var englishPath = Path.Combine(folder, "en.json");
        var english = Read(englishPath, logger);
        if (english == null)
        {
            return null;
        }

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var dayLines = new Dictionary<string, Dictionary<DayOfWeek, List<string>>>(StringComparer.OrdinalIgnoreCase);
        Merge(english, englishPath, sections, lines, dayLines, logger);

        if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            var chosenPath = Path.Combine(folder, $"{language}.json");
            var chosen = Read(chosenPath, logger);
            if (chosen == null)
            {
                logger.Warning($"[Radio Norvinsk] No usable locales/{language}.json, speaking English.");
            }
            else
            {
                Merge(chosen, chosenPath, sections, lines, dayLines, logger);
            }
        }

        return new Locale(sections, lines, dayLines, clock ?? (() => DateTime.Now));
    }

    /// <summary>
    /// One line of the first key in <paramref name="keys"/> that has any today, with each <c>{name}</c> placeholder
    /// replaced. A key says this week's share of its lines, plus its lines for the day of the week: each of them once,
    /// in random order, before any comes back, and never the same one twice in a row. Null when none of the keys has a
    /// line.
    /// </summary>
    public string Line(IEnumerable<string> keys, params (string Name, string Value)[] values)
    {
        var today = (_clock() - DayStart).Date;
        foreach (var key in keys)
        {
            var pool = Pool(key, today);
            if (pool.Count == 0)
            {
                continue;
            }

            string line;
            lock (_lock)
            {
                line = Draw(key, pool);
            }

            return Fill(line, values);
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

    // What a key can say on the radio's day: this week's share of its lines, and the day's own lines.
    private List<string> Pool(string key, DateTime today)
    {
        var pool = new List<string>();
        if (_lines.TryGetValue(key, out var lines))
        {
            // DateTime.MinValue is a Monday: whole weeks since then start on Mondays.
            pool.AddRange(ThisWeek(key, lines, today.Ticks / TimeSpan.TicksPerDay / 7));
        }

        if (_dayLines.TryGetValue(key, out var days) && days.TryGetValue(today.DayOfWeek, out var daily))
        {
            pool.AddRange(daily);
        }

        return pool;
    }

    // A week's share of a key's lines: a slice of the lines in an order fixed by the key, the next slice the next week.
    // A slice is at most half the lines, so a week never repeats the one before, and every line comes back in turn.
    private static List<string> ThisWeek(string key, List<string> lines, long week)
    {
        var slices = Math.Clamp(lines.Count / MinWeeklyLines, 1, RotationWeeks);
        if (slices == 1)
        {
            return lines;
        }

        var size = lines.Count / slices;
        var order = Shuffle(Enumerable.Range(0, lines.Count).ToList(), new Random(Seed(key)));
        var start = (int)(week * size % lines.Count);
        return Enumerable.Range(start, size).Select(i => lines[order[i % lines.Count]]).ToList();
    }

    // The bag of lines a key has left to say: refilled, in a new random order, once empty or once the pool changes
    // (a new day or a new week).
    private string Draw(string key, List<string> pool)
    {
        if (!_bags.TryGetValue(key, out var bag))
        {
            _bags[key] = bag = new Bag();
        }

        if (!bag.Pool.SequenceEqual(pool))
        {
            bag.Pool = pool;
            bag.Left.Clear();
        }

        if (bag.Left.Count == 0)
        {
            bag.Left.AddRange(Shuffle(pool.ToList(), _random));

            // Lines are drawn from the end: the line just said goes to the front instead.
            if (bag.Left.Count > 1 && bag.Left[^1] == bag.Last)
            {
                (bag.Left[0], bag.Left[^1]) = (bag.Left[^1], bag.Left[0]);
            }
        }

        var line = bag.Left[^1];
        bag.Left.RemoveAt(bag.Left.Count - 1);
        bag.Last = line;
        return line;
    }

    private static List<T> Shuffle<T>(List<T> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    // The same number for the same key on every start and every machine, unlike string.GetHashCode (FNV-1a).
    private static int Seed(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key.ToLowerInvariant())
        {
            hash = unchecked((hash ^ c) * 16777619u);
        }

        return unchecked((int)hash);
    }

    private static string Fill(string text, (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value ?? "");
        }

        return text;
    }

    private static void Merge(
        LocaleFile file,
        string path,
        Dictionary<string, Dictionary<string, string>> sections,
        Dictionary<string, List<string>> lines,
        Dictionary<string, Dictionary<DayOfWeek, List<string>>> dayLines,
        ISptLogger<RadioNorvinskMod> logger)
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

                // A key's day lines go with its lines: English ones would turn up among another language's.
                dayLines.Remove(key);
            }
        }

        foreach (var (dayName, keys) in file.Days ?? new())
        {
            if (!DayNames.TryGetValue(dayName, out var day))
            {
                logger.Warning($"[Radio Norvinsk] {path}: \"{dayName}\" under days is not a day of the week (monday to sunday).");
                continue;
            }

            foreach (var (key, variants) in keys ?? new())
            {
                if (variants is not { Count: > 0 })
                {
                    continue;
                }

                if (!dayLines.TryGetValue(key, out var byDay))
                {
                    dayLines[key] = byDay = new Dictionary<DayOfWeek, List<string>>();
                }

                byDay[day] = variants;
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

    private sealed class Bag
    {
        public List<string> Pool { get; set; } = new();
        public List<string> Left { get; } = new();
        public string Last { get; set; }
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

        /// <summary>Extra lines for a day of the week: <c>days.friday.connect</c> joins <c>lines.connect</c> on Fridays.</summary>
        public Dictionary<string, Dictionary<string, List<string>>> Days { get; set; }

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
