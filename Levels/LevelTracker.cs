using System;
using System.Collections.Generic;
using RadioNorvinsk.Chronicle;

namespace RadioNorvinsk.Levels;

/// <summary>
/// Announces each level once. A level can be seen twice (end of raid, then a quest reward recalculating it),
/// so the tracker remembers the highest level already announced for each profile.
/// </summary>
internal sealed class LevelTracker(Chronicler chronicler)
{
    private readonly Dictionary<string, int> _known = new();
    private readonly object _lock = new();

    /// <summary>A level reached outside a raid: announced on its own when it is new.</summary>
    public void Report(string profileId, string nickname, int? prestige, int? before, int? after)
    {
        if (!string.IsNullOrEmpty(nickname) && Claim(profileId, prestige ?? 0, before, after) is int level)
        {
            chronicler.LeveledUp(nickname, level, prestige ?? 0);
        }
    }

    /// <summary>
    /// The level to announce, or null when it isn't new. The caller announces it: the end of a raid puts it
    /// in the player's end-of-raid message.
    /// </summary>
    public int? Claim(string profileId, int prestige, int? before, int? after)
    {
        if (string.IsNullOrEmpty(profileId) || after is not int now)
        {
            return null;
        }

        // Prestiging sends the profile back to level 1 under the same id. Counting levels per prestige
        // lets the climb back up be announced, instead of waiting for the profile to pass its old level.
        var key = $"{profileId}/{prestige}";
        lock (_lock)
        {
            var known = _known.TryGetValue(key, out var seen) ? seen : before ?? now;
            _known[key] = Math.Max(known, now);
            return now > known ? now : null;
        }
    }
}
