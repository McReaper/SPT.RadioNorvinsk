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

    public void Report(string profileId, string nickname, int? prestige, int? before, int? after)
    {
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(nickname) || after is not int now)
        {
            return;
        }

        // Prestiging sends the profile back to level 1 under the same id. Counting levels per prestige
        // lets the climb back up be announced, instead of waiting for the profile to pass its old level.
        var rank = prestige ?? 0;
        var key = $"{profileId}/{rank}";
        lock (_lock)
        {
            var known = _known.TryGetValue(key, out var seen) ? seen : before ?? now;
            _known[key] = Math.Max(known, now);
            if (now <= known)
            {
                return;
            }
        }

        chronicler.LeveledUp(nickname, now, rank);
    }
}
