using System;
using RadioNorvinsk.Levels;
using RadioNorvinsk.Raids;
using RadioNorvinsk.Sessions;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Patches;

/// <summary>Where the patches reach the mod: set once at startup, before they are enabled.</summary>
internal static class PatchContext
{
    internal static SessionTracker Sessions;
    internal static RaidReporter Raids;
    internal static LevelTracker Levels;
    internal static ISptLogger<RadioNorvinskMod> Logger;

    // Runs inside the server's own request and websocket handlers: never let the radio break a player's game.
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
