#!/usr/bin/env bash
set -euo pipefail
rm -f "${XDG_DATA_HOME:-$HOME/.local/share}/applications/harbor.desktop" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps/harbor.svg"
rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/harbor-app"
printf 'Application removed. Harbor server data and configuration preserved.\n'
