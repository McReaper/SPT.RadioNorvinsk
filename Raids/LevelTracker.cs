using System;
using System.Collections.Generic;
using RadioNorvinsk.Chronicle;

namespace RadioNorvinsk.Raids;

/// <summary>
/// Announces each level once. A level can be seen twice (end of raid, then a quest reward recalculating it),
/// so the tracker remembers the highest level already announced for each profile.
/// </summary>
internal sealed class LevelTracker(Chronicler chronicler)
{
    private readonly Dictionary<string, int> _known = new();
    private readonly object _lock = new();

    public void Report(string profileId, string nickname, int? before, int? after)
    {
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(nickname) || after is not int now)
        {
            return;
        }

        lock (_lock)
        {
            var known = _known.TryGetValue(profileId, out var seen) ? seen : before ?? now;
            _known[profileId] = Math.Max(known, now);
            if (now <= known)
            {
                return;
            }
        }

        chronicler.LeveledUp(nickname, now);
    }
}
