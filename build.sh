#! /usr/bin/env bash
# Build a Bopl mod and prepare artifacts.
# Usage: ./build.sh [ModName]     (default: HelloBopl)
set -euo pipefail

MOD=${1:-HelloBopl}
DIR="$(cd "$(dirname "$0")" && pwd)"
PROJ="$DIR/src/$MOD/$MOD.csproj"
BIN="$DIR/src/$MOD/bin/Release/netstandard2.1"
OUT="$DIR/dist/$MOD"
GAME_PLUGINS="$HOME/.local/share/Steam/steamapps/common/Bopl Battle/BepInEx/plugins"

dotnet build "$PROJ" -c Release 2>&1 | tee /tmp/opencode/$MOD.build.log | grep -E 'error|warn' && echo "^ investigate" || true
[[ -f "$BIN/$MOD.dll" ]] || { echo "BUILD FAILED"; exit 1; }

# Thunderstore package: manifest.json + README + icon + plugins/<Mod>.dll — ship ONLY our dll
rm -rf "$OUT"; mkdir -p "$OUT/plugins"
cp "$BIN/$MOD.dll" "$OUT/plugins/"
cp "$DIR/src/$MOD/manifest.json" "$OUT/"
cp "$DIR/src/$MOD/README.md" "$OUT/"
cp "$DIR/src/$MOD/icon.png" "$OUT/"
MOD="$MOD" python3 - <<'EOF'
import os, zipfile
mod = os.environ["MOD"]
out = f"dist/{mod}/{mod}.zip"
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in ("manifest.json", "README.md", "icon.png"):
        z.write(f"dist/{mod}/{f}")
    z.write(f"dist/{mod}/plugins/{mod}.dll", f"plugins/{mod}.dll")
EOF
echo "== dist: $OUT ($MOD/$MOD.zip ready for Thunderstore upload)"

# Local quick-test install (optional)
if [[ -d "$GAME_PLUGINS" ]]; then
  cp "$OUT/plugins/$MOD.dll" "$GAME_PLUGINS/"
  echo "== copied to $GAME_PLUGINS (restart the game; watch BepInEx/LogOutput.log)"
fi

# r2modman profile deploy (primary test path on this machine)
PROFILE="$HOME/.config/r2modmanPlus-local/BoplBattle/profiles/Default/BepInEx/plugins"
if [[ -d "$PROFILE" ]]; then
  cp "$OUT/plugins/$MOD.dll" "$PROFILE/Unknown-$MOD.dll"
  echo "== deployed to r2modman profile: $PROFILE/Unknown-$MOD.dll (restart the game)"
fi
