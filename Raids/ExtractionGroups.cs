using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RadioNorvinsk.Chronicle;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Raids;

/// <summary>
/// Holds a Fika squad's extractions back until its raid is over, so they go out as one message. Fika doesn't let
/// the host leave while anyone else is still in the raid: the match ending means everybody is out.
/// Deaths don't wait, they go out as they happen.
/// </summary>
internal sealed class ExtractionGroups(Chronicler chronicler, ISptLogger<RadioNorvinskMod> logger)
{
    // The host's own end of raid can reach the server just after its match is gone.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    // Session id -> match id, and match id -> the extractions waiting for that match to end.
    private readonly Dictionary<string, string> _matchOf = new();
    private readonly Dictionary<string, Group> _groups = new();
    private readonly object _lock = new();

    /// <summary>The match's raid is running, with these players in it.</summary>
    public void Started(string matchId, IEnumerable<string> players)
    {
        lock (_lock)
        {
            _groups.TryAdd(matchId, new Group());
            foreach (var player in players)
            {
                _matchOf[player] = matchId;
            }
        }
    }

    /// <summary>
    /// Keeps the extraction for the end of the player's raid. False when the player isn't in a raid this tracks:
    /// the caller announces it at once.
    /// </summary>
    public bool Hold(string sessionId, RaidEnd end)
    {
        lock (_lock)
        {
            if (!_matchOf.TryGetValue(sessionId, out var matchId) || !_groups.TryGetValue(matchId, out var group))
            {
                return false;
            }

            group.Ends.Add(end);
            return true;
        }
    }

    /// <summary>Fika deleted the match: the host left, or the match timed out.</summary>
    public void Ended(string matchId)
    {
        lock (_lock)
        {
            if (_groups.TryGetValue(matchId, out var group) && group.Flush == null)
            {
                group.Flush = new Timer(_ => Flush(matchId), null, Grace, Timeout.InfiniteTimeSpan);
            }
        }
    }

    // Runs on a timer thread: an exception escaping here would bring the whole server down.
    private void Flush(string matchId)
    {
        try
        {
            List<RaidEnd> ends;
            lock (_lock)
            {
                if (!_groups.Remove(matchId, out var group))
                {
                    return;
                }

                group.Flush.Dispose();
                ends = group.Ends;
                foreach (var player in _matchOf.Where(entry => entry.Value == matchId).Select(entry => entry.Key).ToList())
                {
                    _matchOf.Remove(player);
                }
            }

            if (ends.Count > 0)
            {
                chronicler.SquadExtracted(ends);
            }
        }
        catch (Exception ex)
        {
            logger.Error("[Radio Norvinsk] Could not announce a raid's extractions.", ex);
        }
    }

    private sealed class Group
    {
        public List<RaidEnd> Ends { get; } = [];
        public Timer Flush { get; set; }
    }
}
