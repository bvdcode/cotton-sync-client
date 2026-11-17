#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
package="$(realpath "$1")"
expected_version="$2"
evidence="$(realpath -m "$3")"
app=/opt/cotton-sync/Cotton.Sync.Desktop
mkdir -p "$evidence/fixture" "$evidence/profile"
export HOME="$evidence/profile"
export XDG_CONFIG_HOME="$HOME/.config" XDG_DATA_HOME="$HOME/.local/share" XDG_RUNTIME_DIR="$(mktemp -d)"
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
dbus-update-activation-environment HOME XDG_CONFIG_HOME XDG_DATA_HOME XDG_RUNTIME_DIR DISPLAY XAUTHORITY
unset GNOME_KEYRING_CONTROL SSH_AUTH_SOCK
. "$script_dir/start-secret-service.sh"
server_pid=''
app_pid=''
cleanup() {
  if [ -n "$app_pid" ]; then kill "$app_pid" 2>/dev/null || true; fi
  if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; fi
  wait || true
  rm -rf "$XDG_RUNTIME_DIR"
}
trap cleanup EXIT
old_version="$($app --version)"
test "$old_version" != "$expected_version"
printf 'invalid Debian package\n' > "$evidence/invalid.deb"
if timeout 30s "$app" --update-install-smoke --data-dir "$evidence/rejected-state" \
  --update-installer-path "$evidence/invalid.deb" > "$evidence/rejected-install.log" 2>&1; then
  echo 'Invalid package installation unexpectedly succeeded.' >&2
  exit 1
fi
grep -q 'Result: failed' "$evidence/rejected-install.log"
test "$($app --version)" = "$old_version"
test "$(dpkg-query -W -f='${Version}' cotton-sync-desktop)" = "$old_version"
ln -s "$package" "$evidence/fixture/CottonSync-Linux.deb"
python3 - "$evidence" "$expected_version" <<'PY' > "$evidence/server.log" 2>&1 &
import functools
import hashlib
import http.server
import json
import sys
from pathlib import Path

evidence = Path(sys.argv[1])
package = evidence / 'fixture/CottonSync-Linux.deb'
handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(evidence / 'fixture'))
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), handler)
base = f'http://127.0.0.1:{server.server_port}'
manifest = {
    'schemaVersion': 1, 'product': 'Cotton Sync', 'version': sys.argv[2],
    'tag': 'v' + sys.argv[2], 'commit': 'package-update-test', 'branch': 'main',
    'releaseUrl': base + '/release',
    'assets': [{'name': package.name, 'sha256': hashlib.file_digest(package.open('rb'), 'sha256').hexdigest(),
                'sizeBytes': package.stat().st_size, 'url': base + '/' + package.name}],
}
(evidence / 'fixture/release-manifest.json').write_text(json.dumps(manifest))
(evidence / 'server-url.txt').write_text(base)
server.serve_forever()
PY
server_pid="$!"
for attempt in $(seq 1 100); do
  if [ -s "$evidence/server-url.txt" ]; then break; fi
  kill -0 "$server_pid"
  sleep 0.1
done
server_url="$(cat "$evidence/server-url.txt")"
state="$evidence/state"
mkdir -p "$state" "$XDG_CONFIG_HOME/autostart"
printf 'preserve-update-profile\n' > "$state/profile-marker.txt"
printf '[Desktop Entry]\nType=Application\nName=Cotton Sync\nExec=%s --start-minimized\n' "$app" \
  > "$XDG_CONFIG_HOME/autostart/cotton-sync.desktop"
timeout 120s "$app" --update-discovery-smoke --data-dir "$state" \
  --update-manifest-url "$server_url/release-manifest.json" --expected-update-version "$expected_version" \
  > "$evidence/discovery.log" 2>&1
download="$(python3 - "$state/updates/pending-update.json" <<'PY'
import json
import sys
from pathlib import Path
print(json.loads(Path(sys.argv[1]).read_text())['installerPath'])
PY
)"
timeout 180s "$app" --update-install-smoke --data-dir "$state" --update-installer-path "$download" \
  > "$evidence/install.log" 2>&1
for attempt in $(seq 1 100); do
  app_pid="$(python3 - "$app" "$state" <<'PY'
import sys
from pathlib import Path
expected = [sys.argv[1], '--data-dir', sys.argv[2]]
for entry in Path('/proc').iterdir():
    if entry.name.isdigit():
        try:
            arguments = (entry / 'cmdline').read_bytes().rstrip(b'\0').decode().split('\0')
            if arguments == expected:
                print(entry.name)
                break
        except (OSError, UnicodeError):
            continue
PY
)"
  if [ -n "$app_pid" ]; then break; fi
  sleep 0.2
done
test -n "$app_pid"
timeout 30s xdotool search --sync --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null
test "$($app --version)" = "$expected_version"
test "$(dpkg-query -W -f='${Version}' cotton-sync-desktop)" = "$expected_version"
test "$(cat "$state/profile-marker.txt")" = preserve-update-profile
test -f "$XDG_CONFIG_HOME/autostart/cotton-sync.desktop"
test ! -e "$state/updates/pending-update.json"
"$script_dir/verify-checksums.sh" /opt/cotton-sync > "$evidence/checksums.log"
printf 'PASS: %s -> %s; invalid package rejection, discovery, verified download, authorized package install, restart and profile preservation\n' \
  "$old_version" "$expected_version"
