using System;
using System.Reflection;
using FikaServer.Services;
using HarmonyLib;
using RadioNorvinsk.Raids;
using RadioNorvinsk.Sessions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;

namespace RadioNorvinsk.Patches;

/// <summary>Where the patches reach the mod: set once at startup, before they are enabled.</summary>
internal static class PatchContext
{
    internal static SessionTracker Sessions;
    internal static RaidReporter Raids;
    internal static LevelTracker Levels;
    internal static ISptLogger<RadioNorvinskMod> Logger;

    // Runs inside Fika's websocket handlers: never let the radio break a player's connection.
    internal static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger?.Error($"[Radio Norvinsk] {what} failed.", ex);
        }
    }
}

/// <summary>
/// Fika calls <see cref="PresenceService.AddPlayerPresence"/> when a game opens its notification
/// websocket, right after the player logs in with their profile.
/// </summary>
public class PlayerConnectedPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(PresenceService), nameof(PresenceService.AddPlayerPresence));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID)
    {
        PatchContext.Guard("Player connection", () => PatchContext.Sessions?.OnConnected(sessionID));
    }
}

/// <summary>Fika calls <see cref="PresenceService.RemovePlayerPresence"/> when that websocket closes.</summary>
public class PlayerDisconnectedPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(PresenceService), nameof(PresenceService.RemovePlayerPresence));
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionID)
    {
        PatchContext.Guard("Player disconnection", () => PatchContext.Sessions?.OnDisconnected(sessionID));
    }
}
