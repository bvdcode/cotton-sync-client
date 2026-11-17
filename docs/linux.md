# Linux desktop support

The Linux x64 client synchronizes full local copies with Cotton Cloud. Windows Cloud Files placeholders, online-only files and Explorer commands are Windows features.

| Feature | Linux behavior |
| --- | --- |
| Synchronization | Uploads, downloads, edits, renames, moves, deletion and cloud trash restoration use the shared synchronization engine. Local file watching and server events request automatic synchronization. |
| Session storage | Refresh credentials use the desktop Secret Service. Cancellation during token refresh preserves the replacement session across restart. |
| Tray | KDE Plasma and other desktops exposing a StatusNotifier host receive the icon and native Show/Quit menu. Closing the window hides it while the tray is available. Without a host, the window remains accessible and closing it exits the application. Tray host changes are detected while the client runs. |
| Autostart | The preference manages the user XDG autostart entry. |
| Notifications | Native desktop notifications use the system notification service. |
| Debian updates | The installed `/opt/cotton-sync` package checks for releases and downloads the Debian update, verifies its size and SHA-256, then installs on request through PackageKit. Polkit requests administrator authorization. The client restarts after successful installation. |
| Archive updates | Release checks are available; replace the extracted archive manually. |

Linux versions through 0.1.133 cannot install updates from the application. Install the current Debian package once to enable package updates from the client.

## Validation

Linux CI runs the solution tests with an unlocked Secret Service, real-server full-mirror acceptance, session rotation and restart checks, KDE Plasma/X11 tray lifecycle checks, GUI screenshots, package installation/removal and package update discovery, download, installation and restart.

The KDE check starts the client before the tray host, verifies icon pixels and the native menu, hides and reopens the window, restarts the tray host, checks single-instance activation and exits through Quit.

The file set includes empty files, an 8 MiB file, Cyrillic and accented names, emoji, spaces and nested directories. Two clients verify content hashes after synchronization, automatic edits, restart, deletion and restoration. Every live run uses a separate remote folder and removes its test data and sessions afterward.

Wayland and a complete Kubuntu login session remain separate validation targets. Headless package installation tests run with administrator privileges; the interactive authorization dialog requires a logged-in desktop session.
