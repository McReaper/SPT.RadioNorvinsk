using System.Reflection;
using FikaServer.Services;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;

namespace RadioNorvinsk.Patches;

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

/// <summary>
/// The game calls /client/game/logout when the player quits from its menu: its very last request.
/// A crash sends nothing, and is left to the reconnection grace delay.
/// </summary>
public class PlayerLoggedOutPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GameCallbacks), nameof(GameCallbacks.GameLogout));
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionID)
    {
        PatchContext.Guard("Player logout", () => PatchContext.Sessions?.OnLoggedOut(sessionID));
    }
}
