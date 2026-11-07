# SPT.RadioNorvinsk

Server mod for **SPT 4.1.6 + Fika** that gives your server a chronicler. **Radio Norvinsk** posts the notable moments of your friends' sessions to a Discord channel, with lines picked at random, and never notifies anyone.

Server-side only: players install nothing.

## What it posts

| Event | When | Switch |
|---|---|---|
| Connection | A player's game logs in with their profile. | `events.connect` |
| Disconnection | A player has been gone for `reconnectGraceMinutes` (10 by default). The message gives the session's length. | `events.disconnect` |

A player who comes back within the grace delay (crash, game restart, slow PC) stays in the same session, and nothing is posted either way.

Planned: raid start, death, level up, boss kill, extraction.

**No pings, no notifications.** Every message goes out with Discord's *suppress notifications* flag and an empty `allowed_mentions`. Nobody gets pinged, not even by a nickname like `@everyone`.

## Requirements

- SPT 4.1.6
- Fika server 2.4.x. The mod hooks Fika's presence service, so it refuses to load without it.

## Install

1. Copy `RadioNorvinsk.dll` and the `locales` folder to `<SPT>/SPT_Runtime/user/mods/RadioNorvinsk/`.
2. Start the server once. It creates `config/config.json` in that folder and logs a warning.
3. Create a webhook in Discord (*channel settings > Integrations > Webhooks > New Webhook*), copy its URL and paste it as `webhookUrl`.
4. Restart the server. The console prints:

```
[Radio Norvinsk] On the air, in 'en'.
```

The webhook URL is a secret: anyone who has it can post in the channel.

## Configuration

`config/config.json`, created with these defaults. Comments are allowed. The mod never rewrites the file once it exists.

| Key | Default | Meaning |
|---|---|---|
| `webhookUrl` | `""` | Discord webhook to post to. Empty: the radio stays off. |
| `language` | `"en"` | A file name in `locales/`, without `.json`. |
| `persona.name` | `"Radio Norvinsk"` | Name shown on each message. |
| `persona.avatarUrl` | `""` | Public image URL shown on each message. Empty: the webhook's own avatar. |
| `events.connect` | `true` | Post connections. |
| `events.disconnect` | `true` | Post disconnections. |
| `reconnectGraceMinutes` | `10` | How long a player can be gone and still be in the same session. |

## Languages

`en` (default) and `fr` ship with the mod. To add one, copy `locales/en.json` to `locales/<code>.json`, translate it and set `language` to `<code>`. Any key the file lacks is said in English.

Each key under `lines` holds several variants. The radio picks one at random, never the same one twice in a row. Placeholders:

| Key | Placeholders |
|---|---|
| `connect` | `{player}` |
| `disconnect` | `{player}`, `{duration}` (built from `formats.durationHours` or `formats.durationMinutes`) |

## Build

Requires the .NET 10 SDK and an SPT 4.1.6 install with Fika.

```sh
dotnet build -c Release -p:SPTPath="C:\SPT"
# add -p:InstallToSPT=true to copy the DLL and locales into SPT_Runtime/user/mods/RadioNorvinsk
```

If the repo sits in `<SPT>/Development/SPT.RadioNorvinsk`, `SPTPath` can be omitted.
