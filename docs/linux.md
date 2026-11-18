# Linux desktop support

The Linux x64 client synchronizes full local copies with Cotton Cloud. Windows Cloud Files placeholders, online-only files and Explorer commands are Windows features.

| Feature | Linux behavior |
| --- | --- |
| Synchronization | Uploads, downloads, edits, renames, moves, deletion and cloud trash restoration use the shared synchronization engine. Local file watching and server events request automatic synchronization. |
| Session storage | The Debian package requires GNOME Keyring or the KDE 6 wallet service. It installs GNOME Keyring when neither is installed, preserving an existing KDE provider. Refresh credentials use the active desktop Secret Service. A locked or temporarily unavailable keyring keeps the saved session pending and retries restoration after unlock. Protection errors preserve the stored credentials. Cancellation during token refresh preserves the replacement session across restart. |
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

Full Kubuntu 24.04 / Plasma 5.27 validation passed the tray lifecycle in both X11 and Wayland sessions. The stock desktop had no Secret Service provider; installing GNOME Keyring enabled secure sign-in. The complete two-client cycles in X11 and Wayland passed uploads, downloads, edits, pause/resume, session restoration, rename with immediate edit, deletion and trash restoration, with exact contents and final idle state. A normal password login after reboot automatically unlocked the login keyring and restored the application account through XDG autostart.

Full Kubuntu 26.04 desktop validation used Plasma 6.6 with a Wayland login session; the client renders through XWayland. Tray registration, icon pixels, native Show/Quit, close-to-tray, reopening and single-instance activation passed. Native KWallet Secret Service storage was checked without an additional GNOME keyring. A password login after reboot starts the client through XDG autostart. Delaying wallet unlock beyond the startup deadline preserves the saved session and restores synchronization automatically after unlock.

The complete native-KWallet synchronization cycle did not pass on the stock Kubuntu 26.04 wallet packages (KWallet 6.24.0 with libsecret 0.21.7). Repeated Secret Service operations intermittently fail during encrypted session negotiation. KDE fixed the interoperability defect in [KWallet commit 307db62](https://github.com/KDE/kwallet/commit/307db62637ac2ca78ff670d746433be9381a923b), included in KWallet 6.25.0. Installing another Secret Service provider can change which wallet is selected at the next login. The package accepts an existing KDE 6 wallet and does not force that switch. Client session preservation does not repair that system dependency; this configuration remains an open validation failure.

Interactive Debian update validation covered cancellation, retry, ordinary-user Polkit authorization, PackageKit replacement, automatic restart, retained login and a subsequent up-to-date check. The older-version update fixture contained the current updater. Separately, the published 0.1.134 Debian package discovered and installed the 0.1.135 candidate from a loopback test source on Kubuntu 24.04. Native Polkit authorization, package replacement, automatic restart, retained login and folder configuration, and subsequent changes in both directions passed before publication.

Extended live load validation synchronized 2000 small files and one 1 GiB random file between two clients. Both exact path sets, all SHA-256 hashes and final idle state passed. The combined two-client test process peaked at approximately 412 MiB. The run overlapped desktop provisioning and temporary server errors, so elapsed time is not a throughput benchmark.

Eight active-transfer cases cover overwrite, deletion, file rename and parent-directory rename during upload and during a receiver's download. Restart during upload, pause/resume and recovery from interrupted HTTP requests passed. A separate full-desktop check disconnected the virtual machine's network adapter, created local and cloud changes during the outage, and verified automatic recovery with exact uploaded content after reconnection.

Two receiving-side cases edit a file during download using an in-place write or atomic replacement, retaining its size and modification time. Both byte versions are preserved; after resolving the conflicting names, both clients converge to exact content. Conflicting edits require resolution rather than silently replacing either version.

Download recovery checks cover a remote version changing between chunks, a socket reset after a partial response, and an interrupted synchronization started by a server event. A changed remote version preserves the previous local file and baseline until a fresh download completes. Temporary failures retry automatically for the affected folder; successful folders and permission failures are not repeatedly retried.

The release gate runs active-transfer and receiving-side edit cases. The 2000-file/1-GiB load case is available as the manual `linux_load_validation` workflow input and requires more than 1 GiB of free account quota. Acceptance checks verify available quota before transferring files and remove their own files from both the active tree and trash. The exclusive CI account also clears abandoned acceptance namespaces and verifies that storage usage returns to its starting value. Full desktop validation uses a virtual GPU and does not establish compatibility with every physical GPU or Linux distribution.
