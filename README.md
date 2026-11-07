# SPT.RadioNorvinsk

Server mod for **SPT 4.1.6 + Fika** that gives your server a chronicler. **Radio Norvinsk** posts the notable moments of your friends' sessions to a Discord channel, with lines picked at random, and never notifies anyone.

Server-side only: players install nothing.

## What it posts

| Event | When | Switch |
|---|---|---|
| Connection | A player's game logs in with their profile. | `events.connect` |
| Disconnection | A player quits the game from its menu, or a game that dropped stays gone for `reconnectGraceMinutes` (10 by default). The message gives the session's length. | `events.disconnect` |
| Raid start | A PMC raid starts: one message for the whole Fika squad, with the map and the in-raid time. | `events.raidStart` |
| Death | A player dies in a PMC raid: killer and what they were (scav, raider, boss...), weapon, body part, map, time in raid. The cause shows when it isn't the killer's bullet (bleeding, fall, mine...). Missing in action counts as a death. | `events.death` |
| Extraction | A player extracts from a PMC raid, run-throughs included. | `events.extraction` |
| Level up | A player gains a level, after a raid or a quest reward. | `events.levelUp` |
| Boss kill | A player kills a boss (Goons included) in a PMC raid: weapon, distance, body part. | `events.bossKill` |

Scav raids are ignored entirely.

A game that drops without quitting (crash, lost connection, slow PC) and comes back within the grace delay stays in the same session, and nothing is posted either way.

Deaths get lines picked for the situation when one applies, from the most specific to the plain one: suicide (own grenade), friendly fire, fall, bleeding, explosion, killed by a boss, within the first 2 minutes, head-eyes, by a scav.

Each message is logged in the server console, with Discord's response time: `[Radio Norvinsk] Posted death.headEyes (Reapy) (142 ms).`

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
| `events.raidStart` | `true` | Post PMC raid starts. |
| `events.death` | `true` | Post deaths and missing in action. |
| `events.extraction` | `true` | Post extractions. |
| `events.levelUp` | `true` | Post level-ups. |
| `events.bossKill` | `true` | Post boss kills. |
| `reconnectGraceMinutes` | `10` | How long a game that dropped without quitting can be gone and still be in the same session. |

## Languages

`en` (default) and `fr` ship with the mod. To add one, copy `locales/en.json` to `locales/<code>.json`, translate it and set `language` to `<code>`. Any key the file lacks is said in English.

Each key under `lines` holds several variants. The radio picks one at random, never the same one twice in a row. Placeholders:

| Key | Placeholders |
|---|---|
| `connect` | `{player}` |
| `disconnect` | `{player}`, `{duration}` |
| `raidStartSolo`, `raidStartSquad` | `{player}` (first of the squad), `{squad}`, `{count}`, `{map}`, `{mapAt}`, `{time}` |
| `death`, `death.<situation>` | `{player}`, `{killer}`, `{boss}`, `{weapon}`, `{map}`, `{mapAt}`, `{raidTime}` |
| `mia` | `{player}`, `{map}`, `{mapAt}`, `{raidTime}` |
| `extract`, `extractRunThrough` | `{player}`, `{map}`, `{mapAt}`, `{raidTime}` |
| `levelUp` | `{player}`, `{level}`, `{prestige}` |
| `bossKill` | `{player}`, `{boss}`, `{weapon}`, `{distance}`, `{map}`, `{mapAt}` |

`{mapAt}` is the map with its preposition, from `mapsAt` ("sur Customs", "on Customs"). The other sections (`maps`, `roles`, `bodyParts`, `headSegments`, `causes`, `labels`, `formats`) name what the game only gives as ids. Weapon names come from the game's own translations.

## Build

Requires the .NET 10 SDK and an SPT 4.1.6 install with Fika.

```sh
dotnet build -c Release -p:SPTPath="C:\SPT"
# add -p:InstallToSPT=true to copy the DLL and locales into SPT_Runtime/user/mods/RadioNorvinsk
```

If the repo sits in `<SPT>/Development/SPT.RadioNorvinsk`, `SPTPath` can be omitted.
