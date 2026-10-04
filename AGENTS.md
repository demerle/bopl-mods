# AGENTS.md

Before doing any Bopl Battle mod work:

1. Read `docs/development-notes.md` (written by us — verified project patterns; what to do and what NOT to do).
2. The game code is decompiled locally at `decompiled/` — read decompiled sources to verify ANY game API before guessing (class names, method signatures, field accessibility). Never invent Bopl APIs.
3. Community docs: https://boplmods.reallybad.dev (create/quickstart, create/create). Template repo: https://github.com/Bopl-Battle-Modding-Community/mod-template
4. Game install (local steam): `~/.local/share/Steam/steamapps/common/Bopl Battle/`. Managed DLLs in `BoplBattle_Data/Managed/`. Re-decompile only if the game updates (see below).

Rounds-mod artifacts were moved to `rounds_archive/` for reference only — do not build on them.
