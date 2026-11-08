using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika;
using RadioNorvinsk.Chronicle;
using RadioNorvinsk.Levels;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;

namespace RadioNorvinsk.Raids;

/// <summary>
/// Reads what SPT and Fika know about a raid and hands the chronicler a story.
/// Scav raids are ignored from start to end.
/// </summary>
internal sealed class RaidReporter(
    Chronicler chronicler,
    LevelTracker levels,
    SaveServer saveServer,
    ProfileHelper profileHelper,
    WeatherHelper weatherHelper)
{
    // Raid shorter than this: the "already?!" lines.
    private static readonly TimeSpan QuickDeath = TimeSpan.FromMinutes(2);

    private static readonly HashSet<DamageType> Bleeding = [DamageType.LightBleeding, DamageType.HeavyBleeding];
    private static readonly HashSet<DamageType> Blasts = [DamageType.Landmine, DamageType.Artillery, DamageType.Explosion, DamageType.ThermobaricExplosion];

    // Session id -> map id, from the raid's start: the end-of-raid request doesn't carry the map.
    private readonly ConcurrentDictionary<string, string> _locations = new();
    private readonly ConcurrentDictionary<string, byte> _announcedMatches = new();

    public void OnLocalRaidStart(MongoId sessionId, StartLocalRaidRequestData request)
    {
        if (!string.IsNullOrEmpty(request?.Location))
        {
            _locations[sessionId.ToString()] = request.Location;
        }
    }

    /// <summary>Fika marks a match IN_GAME once its raid has started: one message for the whole squad.</summary>
    public void OnMatchStatus(MongoId matchId, FikaMatch match, EFikaMatchStatus status)
    {
        if (status == EFikaMatchStatus.COMPLETE)
        {
            _announcedMatches.TryRemove(matchId.ToString(), out _);
            return;
        }

        if (status != EFikaMatchStatus.IN_GAME || match == null || match.Side != EFikaSide.PMC
            || !_announcedMatches.TryAdd(matchId.ToString(), 0))
        {
            return;
        }

        var location = match.RaidConfig?.Location ?? "";
        var squad = match.Players
            .ToArray()
            .Where(player => !player.Value.IsSpectator)
            .Select(player => NicknameOf(player.Key))
            .Where(nickname => !string.IsNullOrEmpty(nickname))
            .ToList();

        if (squad.Count == 0 && !string.IsNullOrEmpty(match.HostUsername))
        {
            squad.Add(match.HostUsername);
        }

        chronicler.RaidStarted(location, Clock(location, match.Time), squad);
    }

    public void OnLocalRaidEnd(MongoId sessionId, EndLocalRaidRequestData request)
    {
        var result = request?.Results;
        var profile = result?.Profile;
        var player = profile?.Info?.Nickname;
        if (string.IsNullOrEmpty(player) || string.Equals(profile.Info.Side, "Savage", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var eft = profile.Stats?.Eft;
        _locations.TryRemove(sessionId.ToString(), out var location);
        location ??= eft?.Victims?.FirstOrDefault()?.Location ?? "";
        var playTime = TimeSpan.FromSeconds(result.PlayTime ?? 0);

        // In the order it happened: kills during the raid, then how it ended, then the experience.
        foreach (var victim in eft?.Victims ?? [])
        {
            if (Names.IsBoss(victim.Role))
            {
                chronicler.BossKilled(player, chronicler.Names.Role(victim.Role), chronicler.Names.Weapon(victim.Weapon),
                    victim.Distance, chronicler.Names.Hit(victim.BodyPart, null), victim.Location ?? location);
            }
        }

        switch (result.Result)
        {
            case ExitStatus.KILLED:
                chronicler.Died(Death(sessionId, player, eft, location, playTime));
                break;
            case ExitStatus.MISSINGINACTION:
                chronicler.WentMissing(player, location, playTime);
                break;
            case ExitStatus.SURVIVED:
            case ExitStatus.RUNNER:
                chronicler.Extracted(player, location, playTime, result.Result == ExitStatus.RUNNER);
                break;
        }

        // Before this request is processed, the saved profile still holds the level the raid started at.
        var saved = saveServer.ProfileExists(sessionId) ? saveServer.GetProfile(sessionId)?.CharacterData?.PmcData?.Info : null;
        var after = profileHelper.GetLevelFromExperience(profile.Info.Experience ?? 0);
        levels.Report(sessionId.ToString(), player, saved?.PrestigeLevel ?? profile.Info.PrestigeLevel, saved?.Level, after);
    }

    private DeathStory Death(MongoId sessionId, string player, EftStats eft, string location, TimeSpan playTime)
    {
        var names = chronicler.Names;
        var aggressor = eft?.Aggressor;
        var cause = eft?.DeathCause?.DamageType;
        var role = aggressor?.Role ?? eft?.DeathCause?.Role?.ToString();
        var killerId = aggressor?.ProfileId;
        var self = killerId != null && killerId.Value.ToString() == sessionId.ToString();
        var teammate = !self && killerId != null && saveServer.ProfileExists(killerId.Value);

        var keys = new List<string>();
        string indirectCause = null;
        if (self)
        {
            keys.Add("death.suicide");
        }
        else if (teammate)
        {
            keys.Add("death.friendlyFire");
        }
        else if (cause == DamageType.Fall)
        {
            keys.Add("death.fall");
            indirectCause = names.Cause(cause.ToString());
        }
        else if (cause is { } bleed && Bleeding.Contains(bleed))
        {
            keys.Add("death.bleeding");
            indirectCause = names.Cause(cause.ToString());
        }
        else if (cause is { } blast && Blasts.Contains(blast) && aggressor == null)
        {
            keys.Add("death.explosion");
            indirectCause = names.Cause(cause.ToString());
        }
        else if (aggressor == null && cause is not null and not DamageType.Bullet and not DamageType.Melee)
        {
            keys.Add("death.environment");
            indirectCause = names.Cause(cause.ToString());
        }
        else
        {
            if (Names.IsBoss(role))
            {
                keys.Add("death.boss");
            }

            if (playTime < QuickDeath)
            {
                keys.Add("death.quick");
            }

            if (string.Equals(aggressor?.HeadSegment, "Eyes", StringComparison.OrdinalIgnoreCase))
            {
                keys.Add("death.headEyes");
            }

            if (Names.IsScav(role))
            {
                keys.Add("death.scav");
            }
        }

        keys.Add("death");

        // Bosses' bot names are random Cyrillic: the boss's own name tells the story better.
        var killer = Names.IsBoss(role) ? names.Role(role) : aggressor?.Name;
        var killerWithRole = self || aggressor == null ? null
            : teammate || Names.IsBoss(role) ? killer
            : $"{killer} ({names.Role(role)})";

        var bodyPart = aggressor?.BodyPart ?? eft?.DamageHistory?.LethalDamagePart;

        return new DeathStory(
            player,
            keys.ToArray(),
            killer,
            killerWithRole,
            names.Weapon(aggressor?.WeaponName),
            names.Hit(bodyPart, aggressor?.HeadSegment),
            indirectCause,
            location,
            playTime);
    }

    private string Clock(string location, EFikaTime time)
    {
        // Factory's time of day comes with the map, not with the clock.
        if (string.Equals(location, "factory4_day", StringComparison.OrdinalIgnoreCase))
        {
            return chronicler.Names.Label("factoryDay");
        }

        if (string.Equals(location, "factory4_night", StringComparison.OrdinalIgnoreCase))
        {
            return chronicler.Names.Label("factoryNight");
        }

        var now = weatherHelper.GetInRaidTime();
        return chronicler.Names.Clock(time == EFikaTime.PAST ? now.AddHours(12) : now);
    }

    private string NicknameOf(MongoId profileId)
    {
        return saveServer.ProfileExists(profileId) ? saveServer.GetProfile(profileId)?.CharacterData?.PmcData?.Info?.Nickname : null;
    }
}
