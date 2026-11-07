using System;
using System.Text;
using RadioNorvinsk.Config;
using RadioNorvinsk.Discord;

namespace RadioNorvinsk.Chronicle;

/// <summary>Turns what happened into what the radio says: checks the event's switch, picks a line, posts it.</summary>
internal sealed class Chronicler(EventsConfig events, Locale locale, DiscordWebhook webhook)
{
    public void SessionStarted(string player)
    {
        if (events.Connect)
        {
            Say("connect", ("player", EscapeMarkdown(player)));
        }
    }

    public void SessionEnded(string player, TimeSpan played)
    {
        if (events.Disconnect)
        {
            Say("disconnect", ("player", EscapeMarkdown(player)), ("duration", Duration(played)));
        }
    }

    private void Say(string key, params (string Name, string Value)[] values)
    {
        var line = locale.Line(key, values);
        if (line != null)
        {
            webhook.Post(line);
        }
    }

    private string Duration(TimeSpan played)
    {
        var minutes = Math.Max(1, (int)played.TotalMinutes);
        return minutes < 60
            ? locale.Format("durationMinutes", ("minutes", minutes.ToString()))
            : locale.Format("durationHours", ("hours", (minutes / 60).ToString()), ("minutes", (minutes % 60).ToString("00")));
    }

    // A nickname like "__Dima__" would otherwise turn the rest of the line into italics.
    private static string EscapeMarkdown(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
