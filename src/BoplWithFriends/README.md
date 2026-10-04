# BoplWithFriends

Raises Bopl Battle's 4-player online limit — same idea as the ROUNDS mod
`RoundsWithFriends`, brought to Bopl: **up to 16 players in one online lobby.**

## IMPORTANT

- **Every player in the lobby must install this mod.** The vanilla wire protocol
  only has 4 player slots; this mod extends it, so unmodded clients can't join
  extended lobbies.
- The **host's** `MaxPlayers` setting is used for the match.
- Replays of matches with more than 4 players won't restore all players.

## Config

`Bopl Battle/BepInEx/config/com.deeroot.boplwithfriends.cfg`:

| Key        | Default | Description                       |
| ---------- | ------- | --------------------------------- |
| MaxPlayers | 8       | Lobby capacity (2–16). Host value wins. |

## Install

r2modman/Gale or manually drop `BoplWithFriends.dll` into `Bopl Battle/BepInEx/plugins/`.

## Build

```
./build.sh BoplWithFriends
```

Requires .NET SDK 8+ and a local Bopl Battle install.
