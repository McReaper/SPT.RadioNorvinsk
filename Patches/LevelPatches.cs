using System;
using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace RadioNorvinsk.Patches;

/// <summary>
/// SPT recalculates a PMC's level here after quest rewards and trader interactions (every purchase, and the
/// trader check while the game loads): catches level-ups that happen outside raids.
/// </summary>
public class LevelRecalculatedPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ProfileExtensions), nameof(ProfileExtensions.CalculateLevel));
    }

    [PatchPrefix]
    public static void Prefix(PmcData pmcData, out int? __state)
    {
        __state = pmcData?.Info?.Level;
    }

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, int? __state, int? __result)
    {
        PatchContext.Guard("Level check", () =>
        {
            var info = pmcData?.Info;
            if (info != null && !string.Equals(info.Side, "Savage", StringComparison.OrdinalIgnoreCase))
            {
                PatchContext.Levels?.Report(pmcData.Id?.ToString(), info.Nickname, info.PrestigeLevel, __state, __result);
            }
        });
    }
}
