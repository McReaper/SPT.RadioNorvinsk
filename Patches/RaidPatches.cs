using System.Reflection;
using FikaServer.Models.Enums;
using FikaServer.Services;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace RadioNorvinsk.Patches;

/// <summary>Each player's game calls this when their raid loads: remembers the map for the end of the raid.</summary>
public class LocalRaidStartPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MatchController), nameof(MatchController.StartLocalRaidAsync));
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        PatchContext.Guard("Raid start", () => PatchContext.Raids?.OnLocalRaidStart(sessionId, request));
    }
}

/// <summary>
/// Each player's game calls this when their raid ends, with their profile as the raid left it.
/// A prefix: the saved profile still holds the pre-raid state (level) at this point.
/// </summary>
public class LocalRaidEndPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MatchController), nameof(MatchController.EndLocalRaidAsync));
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        PatchContext.Guard("Raid end", () => PatchContext.Raids?.OnLocalRaidEnd(sessionId, request));
    }
}

/// <summary>The raid host's game reports the match status to Fika: IN_GAME once the raid is running.</summary>
public class MatchStatusPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MatchService), nameof(MatchService.SetMatchStatus));
    }

    [PatchPrefix]
    public static void Prefix(MatchService __instance, MongoId matchId, EFikaMatchStatus status)
    {
        PatchContext.Guard("Match status", () => PatchContext.Raids?.OnMatchStatus(matchId, __instance.GetMatch(matchId), status));
    }
}
