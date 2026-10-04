# HelloBopl

Hello World test mod for Bopl Battle. Proof-of-life plugin: logs a configurable
greeting every time an ability spawns (Debug level, visible in the BepInEx log).

## Install

- Via r2modman/Gale: install from Thunderstore (after publishing), or
- Manual: drop `HelloBopl.dll` into `Bopl Battle/BepInEx/plugins/`.

## Config

`Bopl Battle/BepInEx/config/com.deeroot.hellobopl.cfg`

## Build

```
./build.sh HelloBopl
```

Requires .NET SDK 8+ and a local Bopl Battle install.
