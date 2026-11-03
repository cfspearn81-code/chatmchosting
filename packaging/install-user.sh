#!/usr/bin/env bash
set -euo pipefail
source_dir="$(cd "$(dirname "$0")" && pwd)"
target="${XDG_DATA_HOME:-$HOME/.local/share}/harbor-app"
mkdir -p "$target" "${XDG_DATA_HOME:-$HOME/.local/share}/applications" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps"
if [[ "$source_dir" != "$target" ]]; then cp -a "$source_dir/." "$target/"; fi
python3 - "$target" "${XDG_DATA_HOME:-$HOME/.local/share}/applications/harbor.desktop" <<'PY'
import sys
p=sys.argv[1].replace('\\','\\\\').replace('"','\\"').replace('`','\\`').replace('$','\\$').replace('%','%%')
with open(sys.argv[2],'w') as f: f.write('[Desktop Entry]\nType=Application\nName=Harbor\nComment=Local Minecraft server manager\nExec="'+p+'/harbor"\nIcon=harbor\nTerminal=false\nCategories=Game;Utility;\n')
PY
cp "$target/harbor.svg" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps/harbor.svg"
printf 'Installed Harbor. Server data is stored separately and preserved.\n'
