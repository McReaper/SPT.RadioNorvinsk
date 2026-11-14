using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RadioNorvinsk.Chronicle;
using RadioNorvinsk.Discord;
using SPTarkov.Common.Models.Logging;

namespace RadioNorvinsk.Raids;

/// <summary>
/// Holds a Fika squad's extractions back until its raid is over, so they go out as one message. Fika doesn't let
/// the host leave while anyone else is still in the raid: the match ending means everybody is out.
/// Deaths don't wait, they go out as they happen.
/// Once the raid is over, links it to its recording in Raid Review, which only exists then: in the squad's
/// extraction message, or else in the raid's last message, its last death, edited.
/// </summary>
internal sealed class ExtractionGroups(Chronicler chronicler, ReviewLinks reviews, ISptLogger<RadioNorvinskMod> logger)
{
    // The host's own end of raid can reach the server just after its match is gone.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    // Session id -> match id, and match id -> the extractions waiting for that match to end.
    // Fika's match id is its host's profile id.
    private readonly Dictionary<string, string> _matchOf = new();
    private readonly Dictionary<string, Group> _groups = new();
    private readonly object _lock = new();

    /// <summary>The match's raid is running, with these players in it.</summary>
    public void Started(string matchId, IEnumerable<string> players)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(matchId, out var group))
            {
                group = new Group();
                _groups[matchId] = group;
            }

            _matchOf[matchId] = matchId;
            group.Players.Add(matchId);
            foreach (var player in players)
            {
                _matchOf[player] = matchId;
                group.Players.Add(player);
            }
        }

        reviews.RaidStarted(matchId);
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

    /// <summary>
    /// A player's end of raid went out on its own, a death most of the time: the latest one gets the link to the
    /// raid's recording when nobody extracts. Deaths go out when Fika's client reports them, which can be at the
    /// moment of death (Fika's forceSaveOnDeath) or when the player leaves: whoever comes last is the raid's last.
    /// </summary>
    public void Posted(string sessionId, PostedMessage message)
    {
        lock (_lock)
        {
            if (message != null && _matchOf.TryGetValue(sessionId, out var matchId) && _groups.TryGetValue(matchId, out var group))
            {
                group.LastPosted = message;
            }
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
            PostedMessage lastPosted;
            RaidPlayers raid;
            lock (_lock)
            {
                if (!_groups.Remove(matchId, out var group))
                {
                    return;
                }

                group.Flush.Dispose();
                ends = group.Ends;
                lastPosted = group.LastPosted;
                raid = new RaidPlayers(matchId, group.Players);
                foreach (var player in _matchOf.Where(entry => entry.Value == matchId).Select(entry => entry.Key).ToList())
                {
                    _matchOf.Remove(player);
                }
            }

            // Everybody has left the raid: every game's recording is in Raid Review, or about to be.
            if (ends.Count > 0)
            {
                reviews.Find(raid, link => chronicler.SquadExtracted(ends, link));
            }
            else if (lastPosted != null)
            {
                reviews.Find(raid, link => chronicler.AddReview(lastPosted, link));
            }

            reviews.Forget(matchId);
        }
        catch (Exception ex)
        {
            logger.Error("[Radio Norvinsk] Could not announce a raid's extractions.", ex);
        }
    }

    private sealed class Group
    {
        public List<RaidEnd> Ends { get; } = [];
        public HashSet<string> Players { get; } = [];
        public PostedMessage LastPosted { get; set; }
        public Timer Flush { get; set; }
    }
}
