#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
app="$(realpath "$1")"
evidence="$(realpath -m "$2")"
kde_version="$3"
case "$kde_version" in
  5|6) ;;
  *) echo "Supported KDE versions: 5, 6" >&2; exit 2 ;;
esac
kded_service="org.kde.kded$kde_version"
mkdir -p "$evidence/profile"
export HOME="$evidence/profile"
export XDG_CONFIG_HOME="$HOME/.config" XDG_CACHE_HOME="$HOME/.cache" XDG_DATA_HOME="$HOME/.local/share"
export XDG_CURRENT_DESKTOP=KDE XDG_SESSION_TYPE=x11 QT_QUICK_BACKEND=software KWIN_COMPOSE=N
export XDG_CONFIG_DIRS=/etc/xdg XDG_MENU_PREFIX=plasma- KDE_SESSION_VERSION="$kde_version"
export XDG_RUNTIME_DIR="$(mktemp -d)"
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
unset SESSION_MANAGER
dbus-update-activation-environment HOME XDG_CONFIG_HOME XDG_CACHE_HOME XDG_DATA_HOME XDG_RUNTIME_DIR DISPLAY XAUTHORITY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE
unset GNOME_KEYRING_CONTROL SSH_AUTH_SOCK
. "$script_dir/start-secret-service.sh"
pids=()
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  sleep 1
  for pid in "${pids[@]}"; do kill -KILL "$pid" 2>/dev/null || true; done
  wait || true
  rm -rf "$XDG_RUNTIME_DIR"
}
trap cleanup EXIT
kwin_x11 --replace > "$evidence/kwin.log" 2>&1 &
pids+=("$!")
"$app" --data-dir "$evidence/state" > "$evidence/app.log" 2>&1 &
app_pid="$!"
pids+=("$app_pid")
export COTTON_TRAY_TEST_PID="$app_pid"
timeout 30s xdotool search --sync --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null
echo 'PASS: Window remains reachable before KDE tray host starts'
"kded$kde_version" > "$evidence/kded.log" 2>&1 &
pids+=("$!")
for attempt in $(seq 1 30); do
  if timeout 3s gdbus call --session --dest "$kded_service" --object-path /kded \
    --method "$kded_service.loadModule" statusnotifierwatcher >/dev/null 2>&1; then break; fi
  sleep 0.2
done
mkdir -p "$XDG_CONFIG_HOME"
cat > "$XDG_CONFIG_HOME/plasma-org.kde.plasma.desktop-appletsrc" <<'EOF'
[Containments][1]
formfactor=0
lastScreen=0
location=0
plugin=org.kde.desktop
wallpaperplugin=org.kde.color

[Containments][2]
formfactor=2
lastScreen=0
location=4
plugin=org.kde.panel

[Containments][2][Applets][3]
plugin=org.kde.plasma.systemtray

[Containments][2][Applets][3][Configuration]
SystrayContainmentId=4

[Containments][2][General]
AppletOrder=3

[Containments][4]
formfactor=2
lastScreen=0
location=4
plugin=org.kde.plasma.private.systemtray
EOF
plasmashell --no-respawn > "$evidence/plasma.log" 2>&1 &
pids+=("$!")
for attempt in $(seq 1 60); do
  if timeout 3s gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher \
    --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher IsStatusNotifierHostRegistered \
    2>/dev/null | grep -q true; then break; fi
  sleep 1
done
timeout 3s gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher \
  --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher IsStatusNotifierHostRegistered | grep -q true
/usr/bin/python3 - "$evidence" <<'PY'
import os
import sys
import time
from pathlib import Path
from gi.repository import Gio, GLib

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
app_pid = int(os.environ['COTTON_TRAY_TEST_PID'])

def call(destination, path, interface, method, arguments=None):
    return bus.call_sync(destination, path, interface, method, arguments, None,
                         Gio.DBusCallFlags.NONE, 5000, None).unpack()

def wait_for(predicate, description):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if predicate():
            print('PASS: ' + description, flush=True)
            return
        time.sleep(0.2)
    raise RuntimeError(description)

def names():
    return call('org.freedesktop.DBus', '/org/freedesktop/DBus',
                'org.freedesktop.DBus', 'ListNames')[0]

prefix = f'org.kde.StatusNotifierItem-{app_pid}-'
wait_for(lambda: any(name.startswith(prefix) for name in names()), 'KDE tray service registered')
service = next(name for name in names() if name.startswith(prefix))
def watcher_items():
    return call('org.kde.StatusNotifierWatcher', '/StatusNotifierWatcher',
                'org.freedesktop.DBus.Properties', 'Get',
                GLib.Variant('(ss)', ('org.kde.StatusNotifierWatcher', 'RegisteredStatusNotifierItems')))[0]
wait_for(lambda: any(service in item for item in watcher_items()),
         'KDE watcher contains application tray item')
props = call(service, '/StatusNotifierItem', 'org.freedesktop.DBus.Properties',
             'GetAll', GLib.Variant('(s)', ('org.kde.StatusNotifierItem',)))[0]
assert 'Cotton Sync' in props['Title'], props
assert props['IconPixmap'] and props['IconPixmap'][0][0] > 1, 'Missing tray pixels'
menu = props['Menu']
layout = call(service, menu, 'com.canonical.dbusmenu', 'GetLayout',
              GLib.Variant('(iias)', (0, -1, [])))[1]
items = {child[1].get('label', ''): child[0] for child in layout[2]}
assert 'Show' in items and 'Quit' in items, items
print('PASS: Tray icon pixels and native Show/Quit menu exported', flush=True)
Path(sys.argv[1], 'tray-properties.txt').write_text(str(props) + '\n' + str(layout))
call(service, '/StatusNotifierItem', 'org.kde.StatusNotifierItem', 'Activate',
     GLib.Variant('(ii)', (0, 0)))
print('PASS: Tray activation accepted', flush=True)
Path(sys.argv[1], 'tray-service.txt').write_text(service)
PY
window="$(timeout 30s xdotool search --sync --onlyvisible --pid "$app_pid" --name 'Cotton Sync' | head -1)"
timeout 30s xdotool windowactivate --sync "$window" key alt+F4
sleep 1
kill -0 "$app_pid"
if xdotool search --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null 2>&1; then
  echo 'Window did not hide to tray' >&2; exit 1
fi
timeout 3s gdbus call --session --dest "$kded_service" --object-path /kded \
  --method "$kded_service.unloadModule" statusnotifierwatcher >/dev/null
timeout 30s xdotool search --sync --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null
echo 'PASS: Hidden window reopens when KDE tray host disappears'
timeout 3s gdbus call --session --dest "$kded_service" --object-path /kded \
  --method "$kded_service.loadModule" statusnotifierwatcher >/dev/null
for attempt in $(seq 1 30); do
  if timeout 3s gdbus call --session --dest org.kde.StatusNotifierWatcher --object-path /StatusNotifierWatcher \
    --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierWatcher IsStatusNotifierHostRegistered \
    2>/dev/null | grep -q true; then break; fi
  sleep 0.2
done
sleep 1
timeout 30s xdotool windowactivate --sync "$window" key alt+F4
sleep 1
kill -0 "$app_pid"
if xdotool search --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null 2>&1; then
  echo 'Window did not hide after KDE tray host returned' >&2; exit 1
fi
echo 'PASS: Hide to tray resumes after KDE tray host returns'
service="$(/usr/bin/python3 - <<'PYTHON'
import os
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
items = bus.call_sync('org.kde.StatusNotifierWatcher', '/StatusNotifierWatcher',
                      'org.freedesktop.DBus.Properties', 'Get',
                      GLib.Variant('(ss)', ('org.kde.StatusNotifierWatcher', 'RegisteredStatusNotifierItems')),
                      None, Gio.DBusCallFlags.NONE, 5000, None).unpack()[0]
prefix = 'org.kde.StatusNotifierItem-' + os.environ['COTTON_TRAY_TEST_PID'] + '-'
services = [item.split('/')[0] for item in items if item.startswith(prefix)]
assert len(services) == 1, services
print(services[0])
PYTHON
)"
gdbus call --session --dest "$service" --object-path /StatusNotifierItem \
  --method org.kde.StatusNotifierItem.Activate 0 0 >/dev/null
timeout 30s xdotool search --sync --onlyvisible --pid "$app_pid" --name 'Cotton Sync' >/dev/null
"$app" --data-dir "$evidence/state" > "$evidence/second-instance.log" 2>&1
kill -0 "$app_pid"
sleep 1
ffmpeg -loglevel error -y -f x11grab -video_size 1280x800 -i "$DISPLAY" -frames:v 1 "$evidence/kde-tray.png"
/usr/bin/python3 - "$service" <<'PY'
import sys
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
service = sys.argv[1]
def call(path, method, args):
    return bus.call_sync(service, path, 'com.canonical.dbusmenu', method, args,
                         None, Gio.DBusCallFlags.NONE, 5000, None).unpack()
props = bus.call_sync(service, '/StatusNotifierItem', 'org.freedesktop.DBus.Properties',
                      'GetAll', GLib.Variant('(s)', ('org.kde.StatusNotifierItem',)), None,
                      Gio.DBusCallFlags.NONE, 5000, None).unpack()[0]
menu = props['Menu']
layout = call(menu, 'GetLayout', GLib.Variant('(iias)', (0, -1, [])))[1]
quit_id = next(child[0] for child in layout[2] if child[1].get('label') == 'Quit')
call(menu, 'Event', GLib.Variant('(isvu)', (quit_id, 'clicked', GLib.Variant('i', 0), 0)))
PY
for attempt in $(seq 1 30); do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    wait "$app_pid"
    echo 'PASS: Hide, activate, single instance and native Quit'
    exit 0
  fi
  sleep 0.2
done
echo 'Quit did not stop the application' >&2
exit 1
