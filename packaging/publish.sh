#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts
 dotnet publish src/Harbor.App -c Release -r linux-x64 --self-contained true -o artifacts/harbor-linux-x64
cp packaging/harbor.svg packaging/harbor.desktop packaging/install-user.sh packaging/uninstall-user.sh artifacts/harbor-linux-x64/
cat > artifacts/harbor-linux-x64/harbor <<'LAUNCH'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
exec ./Harbor.App "$@"
LAUNCH
chmod +x artifacts/harbor-linux-x64/{harbor,Harbor.App,install-user.sh,uninstall-user.sh}
tar -czf artifacts/harbor-linux-x64.tar.gz -C artifacts harbor-linux-x64
(cd artifacts && sha256sum harbor-linux-x64.tar.gz > SHA256SUMS)
