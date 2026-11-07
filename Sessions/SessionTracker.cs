using System;
using System.Collections.Generic;
using System.Threading;
using RadioNorvinsk.Chronicle;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace RadioNorvinsk.Sessions;

/// <summary>
/// Turns Fika's connection events into play sessions. A session starts on the first connection and ends
/// when the player quits from the game's menu, or once a game that dropped without quitting has stayed
/// away for the whole grace delay: a crash or a slow PC that comes back within the delay is the same
/// session, and says nothing.
/// </summary>
internal sealed class SessionTracker(
    TimeSpan grace,
    Chronicler chronicler,
    SaveServer saveServer,
    ISptLogger<RadioNorvinskMod> logger)
{
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly object _lock = new();

    public void OnConnected(MongoId sessionId)
    {
        var nickname = NicknameOf(sessionId);
        if (string.IsNullOrEmpty(nickname))
        {
            // A profile whose PMC isn't created yet: nothing to announce.
            return;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId.ToString(), out var session))
            {
                // Back within the grace delay: the session goes on.
                session.CancelEnd();
                return;
            }

            _sessions[sessionId.ToString()] = new Session(nickname, DateTime.UtcNow);
        }

        chronicler.SessionStarted(nickname);
    }

    public void OnDisconnected(MongoId sessionId)
    {
        var key = sessionId.ToString();
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session) || session.EndPending)
            {
                return;
            }

            var leftAt = DateTime.UtcNow;
            session.ScheduleEnd(grace, () => End(key, session, leftAt));
        }
    }

    /// <summary>
    /// The player quit from the game's menu: the session is over, there is nothing to wait for.
    /// The websocket closing right after finds no session and stays silent.
    /// </summary>
    public void OnLoggedOut(MongoId sessionId)
    {
        Session session;
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId.ToString(), out session))
            {
                return;
            }

            // The websocket may have closed first and started the grace delay.
            session.CancelEnd();
        }

        chronicler.SessionEnded(session.Nickname, DateTime.UtcNow - session.StartedAt);
    }

    // Runs on a timer thread: an exception escaping here would bring the whole server down.
    private void End(string key, Session session, DateTime leftAt)
    {
        try
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(key, out var current) || current != session || !session.EndPending)
                {
                    return;
                }

                session.CancelEnd();
                _sessions.Remove(key);
            }

            chronicler.SessionEnded(session.Nickname, leftAt - session.StartedAt);
        }
        catch (Exception ex)
        {
            logger.Error("[Radio Norvinsk] Could not end a session.", ex);
        }
    }

    private string NicknameOf(MongoId sessionId)
    {
        // GetProfile throws on an unknown id.
        return saveServer.ProfileExists(sessionId)
            ? saveServer.GetProfile(sessionId)?.CharacterData?.PmcData?.Info?.Nickname
            : null;
    }

    private sealed class Session(string nickname, DateTime startedAt)
    {
        private Timer _end;

        public string Nickname { get; } = nickname;
        public DateTime StartedAt { get; } = startedAt;
        public bool EndPending => _end != null;

        public void ScheduleEnd(TimeSpan delay, Action end)
        {
            _end = new Timer(_ => end(), null, delay, Timeout.InfiniteTimeSpan);
        }

        public void CancelEnd()
        {
            _end?.Dispose();
            _end = null;
        }
    }
}
