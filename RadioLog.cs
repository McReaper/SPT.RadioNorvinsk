using Spectre.Console;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk;

internal static class RadioLog
{
    /// <summary>
    /// Logs one of the radio's routine lines (on the air, posted, settings added) in its own colour, so they stand
    /// out among the other mods' lines. Warnings, errors and debug lines keep SPT's yellow, red and grey.
    /// </summary>
    public static void OnAir<T>(this ISptLogger<T> logger, string message)
    {
        logger.LogWithColor(message, Color.Tan);
    }
}
