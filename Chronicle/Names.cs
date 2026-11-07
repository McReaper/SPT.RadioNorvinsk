using System;
using System.Collections.Generic;
using System.Text;
using SPTarkov.Server.Core.Services.Locales;

namespace RadioNorvinsk.Chronicle;

/// <summary>Turns game ids (map ids, bot roles, item locale keys, body parts) into words the listener knows.</summary>
internal sealed class Names(Locale locale, LocaleService gameLocales, string language)
{
    // The Goons and the cultist priest don't carry the "boss" prefix but are bosses all the same.
    private static readonly HashSet<string> BossesWithoutPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        "followerBigPipe", "followerBirdEye", "sectantPriest",
    };

    private static readonly HashSet<string> ScavRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "assault", "marksman", "cursedAssault", "assaultGroup", "crazyAssaultEvent",
    };

    private Dictionary<string, string> _items;

    public static bool IsBoss(string role)
    {
        return role != null && (role.StartsWith("boss", StringComparison.OrdinalIgnoreCase) || BossesWithoutPrefix.Contains(role));
    }

    public static bool IsScav(string role)
    {
        return role != null && ScavRoles.Contains(role);
    }

    public string Map(string location)
    {
        return locale.Text("maps", location) ?? location ?? "?";
    }

    /// <summary>The map with its preposition: "sur Customs", "on Customs".</summary>
    public string MapAt(string location)
    {
        return locale.Text("mapsAt", location) ?? Map(location);
    }

    /// <summary>"scav", "PMC USEC", "Reshala"... the role itself when the locale doesn't know it.</summary>
    public string Role(string role)
    {
        return role == null ? null : locale.Text("roles", role) ?? role;
    }

    /// <summary>"tête, yeux", "thorax". Null when the game didn't say.</summary>
    public string Hit(string bodyPart, string headSegment)
    {
        if (string.IsNullOrEmpty(bodyPart) || bodyPart == "Common")
        {
            return null;
        }

        var part = locale.Text("bodyParts", bodyPart) ?? bodyPart;
        var segment = string.IsNullOrEmpty(headSegment) ? null : locale.Text("headSegments", headSegment) ?? headSegment;
        return segment == null ? part : $"{part}, {segment}";
    }

    public string Cause(string damageType)
    {
        return damageType == null ? null : locale.Text("causes", damageType) ?? damageType;
    }

    /// <summary>
    /// A weapon from the game's own translations. The profile stores it as an item locale key
    /// such as "5447a9cd4bdc2dbd208b4567 ShortName". Null when the game has no name for it.
    /// </summary>
    public string Weapon(string localeKey)
    {
        if (string.IsNullOrEmpty(localeKey))
        {
            return null;
        }

        try
        {
            _items ??= gameLocales.GetLocaleDb(language);
        }
        catch (Exception)
        {
            return null;
        }

        if (_items.TryGetValue(localeKey, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var templateId = localeKey.Split(' ')[0];
        return _items.TryGetValue(templateId + " ShortName", out name) || _items.TryGetValue(templateId + " Name", out name)
            ? name
            : null;
    }

    public string Duration(TimeSpan span)
    {
        var seconds = Math.Max(1, (int)span.TotalSeconds);
        if (seconds < 60)
        {
            return locale.Format("durationSeconds", ("seconds", seconds.ToString()));
        }

        var minutes = seconds / 60;
        return minutes < 60
            ? locale.Format("durationMinutes", ("minutes", minutes.ToString()))
            : locale.Format("durationHours", ("hours", (minutes / 60).ToString()), ("minutes", (minutes % 60).ToString("00")));
    }

    public string Distance(double meters)
    {
        return locale.Format("distance", ("meters", Math.Round(meters).ToString("0")));
    }

    public string Clock(DateTime time)
    {
        return locale.Format("clock", ("hours", time.Hour.ToString("00")), ("minutes", time.Minute.ToString("00")));
    }

    /// <summary>"Reapy", "Reapy et Dima", "Reapy, Dima et Kolya".</summary>
    public string List(IReadOnlyList<string> names)
    {
        if (names.Count <= 1)
        {
            return names.Count == 1 ? names[0] : "";
        }

        return string.Join(", ", names, 0, names.Count - 1) + locale.Format("listLastSeparator") + names[^1];
    }

    public string Label(string key)
    {
        return locale.Text("labels", key) ?? key;
    }

    // A nickname like "__Dima__" would otherwise turn the rest of the line into italics.
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
