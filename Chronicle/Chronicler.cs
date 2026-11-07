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
    private const int ExtractionColor = 0x2E7D32;
    private const int LevelUpColor = 0xC9A227;
    private const int BossKillColor = 0xD9622B;

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

    public void Died(DeathStory death)
    {
        if (!events.Death)
        {
            return;
        }

        var killer = Names.Escape(death.Killer);
        var facts = Facts(DeathColor,
            ("killer", death.KillerWithRole == null ? null : Names.Escape(death.KillerWithRole)),
            ("weapon", death.Weapon),
            ("hit", death.Hit),
            ("cause", death.Cause),
            ("map", names.Map(death.Location)),
            ("raidTime", names.Duration(death.PlayTime)));

        Say($"{death.Keys[0]} ({death.Player})", death.Keys, facts,
            ("player", Names.Escape(death.Player)),
            ("killer", killer ?? "?"),
            ("boss", killer ?? "?"),
            ("weapon", death.Weapon ?? locale.Format("unknownWeapon")),
            ("map", names.Map(death.Location)),
            ("mapAt", names.MapAt(death.Location)),
            ("raidTime", names.Duration(death.PlayTime)));
    }

    public void WentMissing(string player, string location, TimeSpan playTime)
    {
        if (!events.Death)
        {
            return;
        }

        var facts = Facts(MissingColor, ("map", names.Map(location)), ("raidTime", names.Duration(playTime)));
        Say($"mia ({player})", ["mia"], facts,
            ("player", Names.Escape(player)),
            ("map", names.Map(location)),
            ("mapAt", names.MapAt(location)),
            ("raidTime", names.Duration(playTime)));
    }

    public void Extracted(string player, string location, TimeSpan playTime, bool runThrough)
    {
        if (!events.Extraction)
        {
            return;
        }

        var facts = Facts(ExtractionColor, ("map", names.Map(location)), ("raidTime", names.Duration(playTime)));
        Say($"extract ({player})", runThrough ? ["extractRunThrough", "extract"] : ["extract"], facts,
            ("player", Names.Escape(player)),
            ("map", names.Map(location)),
            ("mapAt", names.MapAt(location)),
            ("raidTime", names.Duration(playTime)));
    }

    public void LeveledUp(string player, int level)
    {
        if (events.LevelUp)
        {
            Say($"levelUp ({player} {level})", ["levelUp"], Facts(LevelUpColor, ("level", level.ToString())),
                ("player", Names.Escape(player)), ("level", level.ToString()));
        }
    }

    public void BossKilled(string player, string boss, string weapon, double? distance, string hit, string location)
    {
        if (!events.BossKill)
        {
            return;
        }

        var distanceText = distance is > 0 ? names.Distance(distance.Value) : null;
        var facts = Facts(BossKillColor,
            ("boss", Names.Escape(boss)),
            ("weapon", weapon),
            ("distance", distanceText),
            ("hit", hit),
            ("map", names.Map(location)));

        Say($"bossKill ({player}: {boss})", ["bossKill"], facts,
            ("player", Names.Escape(player)),
            ("boss", Names.Escape(boss)),
            ("weapon", weapon ?? locale.Format("unknownWeapon")),
            ("distance", distanceText ?? "?"),
            ("map", names.Map(location)),
            ("mapAt", names.MapAt(location)));
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
