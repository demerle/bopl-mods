# Development Notes (Bopl Battle)

Verified working patterns. Keep entries short; add only what has been built and confirmed working.

## Project Layout

- Mod source: plugin project(s) in `src/` (BepInEx 5 / Unity Mono, `netstandard2.1`)
- Decompiled game code: `decompiled/` (per-class .cs files) — read these to verify any game API before guessing
- Game install (local): `~/.local/share/Steam/steamapps/common/Bopl Battle/`
  - Managed DLLs: `.../BoplBattle_Data/Managed/`
- Build output: `src/<Mod>/bin/Release/netstandard2.1/<Mod>.dll`
- Packaging: `./build.sh <Mod>` → `dist/<Mod>/<Mod>.zip` (manifest.json + README + icon + plugins/dll) + copies dll into the game's `BepInEx/plugins/` if that dir exists.
- Plugin GUID convention: `com.deeroot.<modname>`, log prefix `[DEER]`
- Starter: `src/HelloBopl/` — BepInEx 5 plugin + Harmony postfix on `Ability.Awake` (compile verified; **runtime verified in-game**: plugin loads and patch fires).

## Environment (verified)

- Game: Unity Mono, Unity **2022.3.60f1** (matched UnityEngine.Modules NuGet version).
- .NET SDK 8.0 installed at `~/.local/opt/dotnet`; **set `DOTNET_ROOT=$HOME/.local/opt/dotnet` for global tools** (ilspycmd failed to resolve libhostfxr.so without it).
- Decompiling: `dotnet tool install --global ilspycmd --version 9.1.0.7988` (11.x targets net9, not installed); then
  `ilspycmd -p --nested-directories -o decompiled <game>/BoplBattle_Data/Managed/Assembly-CSharp.dll` (4.0 MB dll → 375 classes).
- DLL reference pattern in csproj: `<GameDir>` property defaults to the local steam path, `HintPath=$(GameDir)/BoplBattle_Data/Managed/Assembly-CSharp.dll`; override with `-p:GameDir=...` if the game moves.
- BepInEx NuGet feed: `https://nuget.bepinex.dev/v3/index.json` (set in `NuGet.Config`). Docs template targets `netstandard2.1`, BepInEx.Core 5.*, PluginInfoProps, BepInEx.Analyzers.
- DON'T copy dependency dlls into the Thunderstore package — only `plugins/<Mod>.dll` plus manifest/README/icon (`dotnet build -o dist` copies references; copy from `bin/` instead).

## Test loop (verified end-to-end)

- Test via r2modman (`BoplBattle/Default` profile); mod dll shows there as `Unknown-<Mod>.dll` (local import) — build.sh now deploys there directly.
- Verified: plugin loads (`[Info : HelloBopl] Plugin com.deeroot.hellobopl is loaded!`), Harmony postfix on `Ability.Awake` fired 3× during one match (one per ability instance spawned in the match).
- `LogDebug` lines DON'T reach the r2modman profile's `LogOutput.log` by default — use `LogInfo` for dev logging.
- DON'T string-interpolate a `ConfigEntry<T>` directly — use `.Value` (interpolating the entry prints the bare type name).
- Run the game → check `~/.config/r2modmanPlus-local/BoplBattle/profiles/Default/BepInEx/LogOutput.log`.

## BoplWithFriends (uncapped player count) — verified architecture

Cap enforcement points (all verified in `decompiled/BoplBattle`):
1. `Constants.MAX_PLAYERS = 4` (mutable static; used by ColorPool array sizing).
2. `SteamManager.CreateLobbyAsync(4)` — Steam lobby capacity (Facepunch static; patched via
   `ref int __0` prefix).
3. Wire protocol: `StartRequestPacket` struct has hardcoded slots p1..p4 (id/color/team/ab1-3);
   `NetworkTools.EncodeStartRequest/ReadStartRequest` byte-encode exactly 105 bytes.
4. Host builds the packet in `SteamManager.HostGame`/`HostNextLevel` (only fills p1..p4).
5. Consumers of the 4 fixed slots: `SteamManager.InitNetworkClient`,
   `CharacterSelectHandler_online.ForceStartGame`/`InitPlayer`, `Host.cs` replay load paths.
6. Input pipeline: per-player input packets (67/83 bytes) are fine, but the host-side
   aggregation loop (`Host.ProcessNetworkPackets`) stores remote inputs into a 4-slot
   `InputPacketQuad` (p1..p4) and `Host.Update` applies only p1..p4 right before
   `Updater.TickSimulation`. Inputs for slots >=5 are dequeued and dropped by vanilla.
7. Spawn positions: `GameSessionHandler.teamSpawns` is `Vec2[4]`; `SpawnPlayers` uses
   `int[4]` counters and `array[list[i].Team]` → breaks for >4 teams.
8. `AchievementHandler.killsThisFrame = new int[4]` (indexed playerId-1).

Our approach (no vanilla size/shape changes):
- Keep vanilla 105-byte packet; **append extension chunk** `[0xAA marker][count]` +
  13 bytes per extra slot (steamId LE, color, team, ab1-3). Host derives extras from
  `connectedPlayers[3..]`; clients decode in a `SteamSocket.OnMessage` prefix patch
  (sizes >105 fall through to Host.ReadPacket otherwise — wrong parser).
- Reimplemented via Harmony prefix-skip: `HostGame`, `HostNextLevel`, `InitNetworkClient`
  (generalized loops over connectedPlayers + extension table), `ForceStartGame`
  (builds Player list incl. slots 5..N via the game's own private `InitPlayer`),
  `SpawnPlayers` (generalized spawn placement: spawn cross x4 + offset waves),
  `ProcessNetworkPackets` (feeds extras into per-slot queues; `Updater.TickSimulation`
  prefix dequeues one input per extra slot per tick — same pacing as vanilla quads).
- `Constants.MAX_PLAYERS` + `killsThisFrame` swapped via reflection at plugin Awake.
- TeamColors ScriptableObject palettes expanded (clone + golden-ratio hue shift) when
  shorter than MaxPlayers, so TeamSelector (cycles modulo palette length) can give every
  player a unique team.

Known limitations / declared breakage:
- All players must run the mod (vanilla can't parse extended packets — by design).
- Replays don't record/restore slots >=5 (vanilla replay path untouched).
- Local/offline couch play beyond 4 is out of scope (InputSystem has 4 bindings; wire
  format has 4 slots). Online is 1 player per client.
- Online char-select screen shows boxes for max 4 remote players (`networkPlayerBoxes`
  inspector array); slots >=5 play fine but have no box UI (cosmetic, patch later).
- Matchmaking "LFM" flow inherits Steam lobby capacity → naturally supports the bigger cap.

Runtime-verification TODO (needs in-game test with >=5 players):
- TeamColors/PlayerColors/ColorPalette actual lengths (logged on menu load by the mod:
  `TeamColors 'X': N entries` lines in LogOutput.log).
- Lobby join past 4, start, spawn positions for 5+, ability select for slots >=5 desync
  behavior, kicks, next-level flow (`HostNextLevel` reimpl), adaptive delay w/o extras in
  `previousInputQuad` (vanilla only reads p1..p4 there — extra delay target ignored).

UI additions (v0.1.1):
- `CSBox_online.Init` reimplemented: team auto-assign was hardcoded `b < 4`; now cycles the
  full (expanded) TeamColors palette so slots 5..N get distinct teams.
- `CharacterSelectHandler_online.Awake` postfix clones `networkPlayerBoxes[0]` (deep hierarchy
  clone + reflection rewiring of non-serialized refs — `Instantiate` drops private refs like
  `animateInOut`/`choosingText`/`readyText`) until the array holds MaxPlayers-1 boxes; clones
  `loadingCircles` in parallel (Update zips the two arrays by index) and lays all boxes out
  evenly with scale `4/N`. Generic helper `CloneWithRefs` in UIPatches.cs is reusable.
- `HandleAbilitySelectUI.Init`: separation = base * 3/(N-1), circles scaled by sqrt(3/(N-1)) —
  keeps the row at vanilla 4-player width for any N.
- Runtime-tunable: box geometry is scene data (invisible to decompile); the mod logs each
  box's rect size/anchors/scale to the BepInEx log on entering the char-select scene — tune
  LayoutBoxes() constants from those numbers after a live test.

## DO / DON'T (verified)

- TBD — add each verified in-game behavior here.
