using System;
using System.Collections.Generic;
using System.Linq;
using RadioNorvinsk.Config;
using RadioNorvinsk.Discord;

namespace RadioNorvinsk.Chronicle;

/// <summary>
/// Turns what happened into what the radio says: checks the event's switch, picks a line, adds the facts, posts.
/// Every name that comes from the game goes through <see cref="Names.Escape"/> before it reaches Discord.
/// </summary>
internal sealed class Chronicler(EventsConfig events, Locale locale, Names names, DiscordWebhook webhook)
{
    private const int RaidStartColor = 0x6B7B3A;
    private const int DeathColor = 0xB3261E;
    private const int MissingColor = 0x6B6B6B;

    public Names Names => names;

    public void SessionStarted(string player)
    {
        if (events.Connect)
        {
            Say($"connect ({player})", ["connect"], null, ("player", Names.Escape(player)));
        }
    }

    public void SessionEnded(string player, TimeSpan played)
    {
        if (events.Disconnect)
        {
            Say($"disconnect ({player})", ["disconnect"], null, ("player", Names.Escape(player)), ("duration", names.Duration(played)));
        }
    }

    public void RaidStarted(string location, string clock, IReadOnlyList<string> squad)
    {
        if (!events.RaidStart || squad.Count == 0)
        {
            return;
        }

        var escaped = squad.Select(Names.Escape).ToList();
        var facts = Facts(RaidStartColor,
            ("map", names.Map(location)),
            ("time", clock),
            (squad.Count == 1 ? "player" : "squad", names.List(escaped)));

        Say($"raidStart ({string.Join(", ", squad)})",
            squad.Count == 1 ? ["raidStartSolo", "raidStartSquad"] : ["raidStartSquad"],
            facts,
            ("player", escaped[0]),
            ("squad", names.List(escaped)),
            ("count", squad.Count.ToString()),
            ("map", names.Map(location)),
            ("mapAt", names.MapAt(location)),
            ("time", clock));
    }

    /// <summary>A level reached outside a raid (quest reward, trader payment). Raids announce theirs in <see cref="RaidEnded"/>.</summary>
    public void LeveledUp(string player, int level, int prestige)
    {
        if (!events.LevelUp)
        {
            return;
        }

        // No facts card: a level is one number.
        Say($"levelUp ({player} {level}, prestige {prestige})", ["levelUp"], null,
            ("player", Names.Escape(player)), ("level", LevelText(level, prestige)));
    }

    /// <summary>
    /// One message per player leaving a PMC raid: how it ended, then the bosses they killed and the level they
    /// reached. When the outcome's switch is off, the first boss, or else the level, opens the message.
    /// </summary>
    public void RaidEnded(RaidEnd end)
    {
        var parts = new List<Spoken>();
        Add(parts, Outcome(end));

        var bosses = events.BossKill ? end.Bosses : [];
        if (bosses.Count > 0 && parts.Count == 0)
        {
            Add(parts, BossLead(end, bosses[0]));
            bosses = bosses.Skip(1).ToList();
        }

        if (bosses.Count == 1)
        {
            Add(parts, BossFollowUp(end, bosses[0]));
        }
        else if (bosses.Count > 1)
        {
            Add(parts, Speak("endBosses", null, ["endBosses"],
                ("player", Names.Escape(end.Player)),
                ("bosses", names.List(bosses.Select(kill => Names.Escape(kill.Boss)).ToList()))));
        }

        if (events.LevelUp && end.Level is int level)
        {
            var key = parts.Count == 0 ? "levelUp" : "endLevel";
            Add(parts, Speak(key, null, [key], ("player", Names.Escape(end.Player)), ("level", LevelText(level, end.Prestige))));
        }

        if (parts.Count == 0)
        {
            return;
        }

        webhook.Post($"{string.Join(" + ", parts.Select(part => part.Key))} ({end.Player})",
            string.Join(" ", parts.Select(part => part.Line)),
            parts[0].Facts);
    }

    private Spoken Outcome(RaidEnd end)
    {
        var player = Names.Escape(end.Player);
        var map = names.Map(end.Location);
        var mapAt = names.MapAt(end.Location);
        var raidTime = names.Duration(end.PlayTime);

        switch (end.Outcome)
        {
            case RaidOutcome.Died when events.Death && end.Death != null:
                var death = end.Death;
                var killer = Names.Escape(death.Killer);
                var facts = Facts(DeathColor,
                    ("killer", death.KillerWithRole == null ? null : Names.Escape(death.KillerWithRole)),
                    ("weapon", death.Weapon),
                    ("hit", death.Hit),
                    ("cause", death.Cause),
                    ("map", map),
                    ("raidTime", raidTime));
                return Speak(death.Keys[0], facts, death.Keys,
                    ("player", player),
                    ("killer", killer ?? "?"),
                    ("boss", killer ?? "?"),
                    ("weapon", death.Weapon ?? locale.Format("unknownWeapon")),
                    ("map", map),
                    ("mapAt", mapAt),
                    ("raidTime", raidTime));

            case RaidOutcome.Missing when events.Death:
                return Speak("mia", Facts(MissingColor, ("map", map), ("raidTime", raidTime)), ["mia"],
                    ("player", player), ("map", map), ("mapAt", mapAt), ("raidTime", raidTime));

            case RaidOutcome.Extracted or RaidOutcome.RunThrough when events.Extraction:
                // No facts card: the line already says who, where and how long, and the card would only repeat it.
                return Speak("extract", null,
                    end.Outcome == RaidOutcome.RunThrough ? ["extractRunThrough", "extract"] : ["extract"],
                    ("player", player), ("map", map), ("mapAt", mapAt), ("raidTime", raidTime));

            default:
                return null;
        }
    }

    // A boss opening the message, when the outcome isn't announced: the full line, as it used to stand alone.
    private Spoken BossLead(RaidEnd end, BossKill kill)
    {
        return Speak("bossKill", null, ["bossKill"],
            ("player", Names.Escape(end.Player)),
            ("boss", Names.Escape(kill.Boss)),
            ("weapon", kill.Weapon ?? locale.Format("unknownWeapon")),
            ("distance", Distance(kill) ?? "?"),
            ("map", names.Map(end.Location)),
            ("mapAt", names.MapAt(end.Location)));
    }

    // A boss after the outcome: a shorter sentence, with what is known of the shot ("RSASS, 15 m, tête").
    private Spoken BossFollowUp(RaidEnd end, BossKill kill)
    {
        var details = string.Join(", ", new[] { kill.Weapon, Distance(kill), kill.Hit }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return Speak("endBoss", null, details.Length > 0 ? ["endBoss"] : ["endBossShort"],
            ("player", Names.Escape(end.Player)),
            ("boss", Names.Escape(kill.Boss)),
            ("details", details));
    }

    private string Distance(BossKill kill)
    {
        return kill.Distance is > 0 ? names.Distance(kill.Distance.Value) : null;
    }

    // The prestige rides along with the level, and only once there is one: "12 (prestige 1)".
    private string LevelText(int level, int prestige)
    {
        return prestige > 0
            ? locale.Format("levelWithPrestige", ("level", level.ToString()), ("prestige", prestige.ToString()))
            : level.ToString();
    }

    private Spoken Speak(string key, Embed facts, IEnumerable<string> keys, params (string Name, string Value)[] values)
    {
        var line = locale.Line(keys, values);
        return line == null ? null : new Spoken(key, line, facts);
    }

    private static void Add(List<Spoken> parts, Spoken part)
    {
        if (part != null)
        {
            parts.Add(part);
        }
    }

    private void Say(string label, IEnumerable<string> keys, Embed facts, params (string Name, string Value)[] values)
    {
        var line = locale.Line(keys, values);
        if (line != null)
        {
            webhook.Post(label, line, facts);
        }
    }

    // Facts without a value are left out: Discord rejects empty fields, and "?" tells nobody anything.
    private Embed Facts(int color, params (string Label, string Value)[] facts)
    {
        var embed = new Embed { Color = color };
        foreach (var (label, value) in facts)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                embed.Fields.Add(new EmbedField { Name = names.Label(label), Value = value });
            }
        }

        return embed;
    }
}

internal enum RaidOutcome
{
    /// <summary>Nothing to announce: left the raid, or a transit to another map.</summary>
    None,
    Extracted,
    RunThrough,
    Died,
    Missing,
}

/// <summary>One player leaving a PMC raid, already turned into words.</summary>
/// <param name="Death">Set when <paramref name="Outcome"/> is <see cref="RaidOutcome.Died"/>.</param>
/// <param name="Level">The level reached in this raid, when it is new.</param>
internal sealed record RaidEnd(
    string Player,
    string Location,
    TimeSpan PlayTime,
    RaidOutcome Outcome,
    DeathStory Death,
    IReadOnlyList<BossKill> Bosses,
    int? Level,
    int Prestige);

/// <param name="Boss">The boss's name, as the listener knows it.</param>
/// <param name="Hit">The body part, in words. Null when unknown.</param>
internal sealed record BossKill(string Boss, string Weapon, double? Distance, string Hit);

/// <summary>One sentence of a message, with the key it came from (for the log) and its facts card, if any.</summary>
internal sealed record Spoken(string Key, string Line, Embed Facts);

/// <summary>Everything the radio needs to tell one death, already turned into words.</summary>
/// <param name="Keys">Line keys from the most specific situation to the plain "death".</param>
/// <param name="Killer">The name used in the line: a nickname, or the boss's name.</param>
/// <param name="KillerWithRole">The name used in the facts, with what the killer was: "Ричи Бывалый (scav sniper)".</param>
internal sealed record DeathStory(
    string Player,
    string[] Keys,
    string Killer,
    string KillerWithRole,
    string Weapon,
    string Hit,
    string Cause,
    string Location,
    TimeSpan PlayTime);
