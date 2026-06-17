# Cotton Sync Windows Client Release Checklist

> Sanitized public checklist. Raw logs, screenshots, account identifiers, app-code approval URLs, local profile paths, private cloud filenames, and diagnostic bundles are intentionally not committed.

**Checklist snapshot:** 61 closed / 0 open / 61 total checkbox rows. 151 total checklist/evidence bullet rows are preserved in this document.

This is the sanitized clean-machine and installed-client pass. It preserves the release gates, live smoke results, packaging checks, tray/autostart behavior, sign-in flow, sync-pair validation, diagnostics, update, uninstall, and CLI smoke coverage without committing private evidence files.

---
Cotton Sync clean Windows release smoke

Use this checklist for a clean Windows user profile or a separate Windows machine. Do not run it on the main development profile unless the goal is explicitly to test upgrade/migration from that profile.

## Release Scope

The first public Windows release is a normal full-mirror sync client. Windows virtual files/placeholders are deferred to `docs/windows-virtual-files-release-checklist.md` and are not a release blocker for this smoke checklist. Do not mark virtual-files work as passed in this checklist; keep that epic open until its own plan is implemented and verified.

## Artifact

- Historical release artifact source:
  - Public release artifacts were verified during QA and later removed from GitHub; raw artifacts and checksums are retained outside the public repository.
  - Asset: `CottonSync-Windows-Setup.exe`
- Record the release commit and version from `release-manifest.json`.
- Release versioning is GitVersion-driven from `MajorMinorPatch`; do not hard-code an expected version in the checklist. Compare the installed UI, diagnostics bundle, installer smoke, and `release-manifest.json` version for the same published release.
  - Current verified release: GitHub Actions run `27652173622` completed successfully, published `Cotton Sync Client 0.0.4` from commit `01683a08c37f975073beb38ba1d82d2c5276b76e`, and exposed `CottonSync-Windows-Setup.exe`, `CottonSync-Windows.zip`, `CottonSync-Windows.tar.gz`, `CottonSync-CLI-Windows.zip`, `CottonSync-Linux.deb`, `CottonSync-Linux.tar.gz`, `release-manifest.json`, and `release-artifact-checksums.sha256`. The downloaded manifest reports `version=0.0.4`, `tag=v0.0.4`, `branch=feature/windows-virtual-files`, and the same commit.
  - Previous verified baseline after the version reset: GitHub Actions run `27596280226`, job `Publish Sync Client Release`, completed successfully; published `sync-client-latest` version `0.0.1` at commit `43b46f04d5e6a6042775e9580b343fbc031f5f39` and exposed `CottonSync-Windows-Setup.exe`, `CottonSync-Windows.zip`, `CottonSync-CLI-Windows.zip`, `release-manifest.json`, and checksums.

## Install

- [x] Installer starts from a normal user account without developer tools.
  - Evidence: GitHub Actions run `27591853188`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed successfully and starts the generated Inno installer with `Start-Process`.
- [x] Windows SmartScreen / Defender prompt, if any, is recorded.
  - Evidence: manual installed-release smoke from `CottonSync-Windows-Setup.exe` published by GitHub Actions run `27596280226` showed no Windows SmartScreen or Defender prompt on this Windows host; the tester also reported no SmartScreen after reboot/logon.
- [x] Install finishes successfully.
  - Evidence: GitHub Actions run `27591853188`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed successfully and verified installed `Cotton.Sync.Desktop.exe`, self-test, checksums, icon, and diagnostics export.
- [x] Start Menu entry `Cotton Sync` is present.
  - Evidence: same Windows installer smoke verifies `Programs\Cotton Sync\Cotton Sync.lnk` and `Programs\Cotton Sync\Uninstall Cotton Sync.lnk`.
- [x] Desktop shortcut behavior is recorded if the installer offers it.
  - Evidence: installer metadata includes optional `Create a desktop shortcut`; Windows CI smoke installs with `/TASKS=` and records the no-desktop-shortcut path for the silent release smoke.
- [x] App launches from Start Menu.
  - Evidence: GitHub Actions run `27592968781`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed successfully after `smoke-start-menu-launch.ps1` launched `Programs\Cotton Sync\Cotton Sync.lnk`, verified it targets the installed `Cotton.Sync.Desktop.exe`, observed the launched process stay alive, and cleaned it up.
- [x] App version shown in the UI matches the published release manifest version.
  - Evidence: published `sync-client-latest` `CottonSync-Windows.zip` from commit `e07cfde921f6eea2488d12bb4724ac772db2b727` was extracted without installing, launched on Windows with `--visual-smoke settings --data-dir <temp>`, and screenshot [windows-ui-version-0.0.1-20260615-212509.png](private-evidence:windows-ui-version-0.0.1-20260615-212509.png) shows `About` version `0.0.1`, matching that release's manifest version.
- [x] Autostart is enabled by default after install.
  - Evidence: GitHub Actions run `27592560981`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed successfully and fails unless the installer-created HKCU Run value `Cotton Sync` equals `"<installed exe>" --start-minimized`.

## First Sign-In

- [x] First launch shows the setup/server step.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~SetupFlow_StartsWithServerStepUntilCottonServerIsVerified"` passed 1/1.
- [x] Server URL accepts `<verified Cotton Cloud server>`.
  - Evidence: live `GET <verified Cotton Cloud server>/api/v1/server/info` returned `product=Cotton Cloud` with an instance hash on attempt 1; `ServerProbe_NormalizesVerifiedBareHostAndEnablesSignIn` covered app-side normalization.
- [x] If firewall prompts, allow it once and retry the same action.
  - Evidence: `ServerProbe_RetriesTransientNetworkFailureAndThenEnablesSignIn` simulates `SocketException(10013)` once and verifies retry of the same server URL.
- [x] Browser approval opens or the app shows a clear waiting/approval state.
  - Evidence: `SignInWithBrowserCommand_CanCancelPendingApproval` verifies `Waiting for approval` and `Approve this sign-in in your browser.` while the approval is pending.
- [x] Approving the login signs the desktop app in.
  - Evidence: `SignInWithBrowserCommand_AppliesSessionAfterPendingApproval` verifies pending approval completion signs the app in and shows the dashboard.
- [x] If TOTP is required, the error and retry flow are human-readable.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~SignInCommand_ShowsHumanTotpRequiredMessage|FullyQualifiedName~SignInCommand_RetriesSuccessfullyAfterTotpRequired"` passed 2/2.
- [x] Signed-in account is visible in the app.
  - Evidence: `SignInCommand_LeavesAddFolderWizardClosedWhenNoSyncPairsExist` and `SignInWithBrowserCommand_AppliesSessionAfterPendingApproval` verify the header shows the signed-in email and `Connected`.

## First Sync Pair

- [x] Add folder opens the wizard.
  - Evidence: `ShowAddSyncPairCommand_OpensLocalStepWithoutPromptingForFolder` verifies the add-folder wizard starts at the local-folder step.
- [x] Choose the Windows Desktop folder as the local folder.
  - Evidence: `AddSyncPairFlow_CreatesDesktopPairAndRequestsInitialSync` selects `<qa Desktop path>` via the folder picker.
- [x] Choose or create the matching Cotton Cloud remote folder.
  - Evidence: `AddSyncPairFlow_CreatesDesktopPairAndRequestsInitialSync` creates and selects `/Desktop`.
- [x] Saving the pair starts sync or leaves a clear `Sync now` path.
  - Evidence: `AddSyncPairFlow_CreatesDesktopPairAndRequestsInitialSync` saves the pair and verifies `GlobalStatus = Sync requested`.
- [x] Initial sync reaches an idle/up-to-date state.
  - Evidence: desktop live two-client smoke against `<verified Cotton Cloud server>` completed with `PASS: Initial desktop sync reached idle/up-to-date. firstStatus=Idle, secondStatus=Idle`; stdout is saved in [cotton-desktop-live-smoke-20260616-052345-481cda88.log](private-evidence:cotton-desktop-live-smoke-20260616-052345-481cda88.log).
- [x] A small file created on the Windows Desktop uploads to Cotton Cloud.
  - Evidence: same desktop live smoke completed `PASS: Desktop local create uploaded and downloaded by the second client.` for `local-upload.txt`, SHA-256 `5bc6cfd05702539126eb93ea5418559ad9f50bdbd0f97f93d450d3dcdfb04c11`.
- [x] A small file uploaded in Cotton Cloud downloads to the Windows Desktop.
  - Evidence: same desktop live smoke completed `PASS: Desktop remote-origin create downloaded by the first client.` for `remote-origin.txt`, SHA-256 `2dbabe171d6758a17da113672b39dd7e5999c4064a0db91da86fdd535d4109f6`.
- [x] Rename local file syncs to Cotton Cloud.
  - Evidence: same desktop live smoke completed `PASS: Desktop local rename propagated to the second client.` for `local-upload.txt` -> `local-renamed.txt`.
- [x] Rename remote file syncs to Windows Desktop.
  - Evidence: same desktop live smoke completed `PASS: Desktop remote-origin rename propagated to the first client.` for `remote-origin.txt` -> `remote-renamed.txt`.
- [x] Delete local file syncs or becomes an explicit action-required state, depending on the configured delete policy.
  - Evidence: same desktop live smoke completed `PASS: Desktop local delete propagated to the second client.` for `local-renamed.txt`.
- [x] Delete remote file syncs or becomes an explicit action-required state, depending on the configured delete policy.
  - Evidence: same desktop live smoke completed `PASS: Desktop remote-origin delete propagated to the first client.` for `remote-renamed.txt`.

## Background, Tray, And Autostart

- [x] Closing the window keeps the app running in the tray.
  - Evidence: `ResolveCloseAction_HidesToTrayWhenTrayLifecycleIsAvailable` verifies close resolves to `HideToTray`.
- [x] Tray `Show` restores exactly one app window.
  - Evidence: `TrayMenu_WiresShowAndQuitToWindowLifecycle` verifies tray `Show` and tray click call `ShowShell`; `Program_RequestsExistingInstanceActivationWhenLockIsHeld` and `App_StartsActivationServerForRunningInstance` verify duplicate launches activate the existing window instead of opening another instance.
- [x] Tray `Sync now` works when the app is idle.
  - Evidence: `TrayMenu_HidesUnavailableActions` verifies tray `Sync now` is routed through `SyncNowCommand`; `SyncNowCommand_RetriesActionRequiredSyncAndClearsMessage` verifies the command calls sync and clears action-required state.
- [x] Tray pause/resume labels are correct.
  - Evidence: `PauseResumeCommands_AreMutuallyAvailable` verifies tray labels switch `Pause` -> `Resume` -> `Pause`.
- [x] Tray `Quit` exits the app.
  - Evidence: `TrayMenu_WiresShowAndQuitToWindowLifecycle` verifies tray `Quit` calls `RequestQuit`; `ResolveCloseAction_ClosesAfterExplicitQuitRequest` verifies explicit quit resolves to close instead of hide.
- [x] Relaunch after tray quit restores the signed-in session.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~DesktopShellControllerHostLifecycleTests"` passed 25/25. The focused `LoadAsync_RestoresSignedInSessionAfterControllerRelaunch` case starts with an empty token store, signs in, saves preferences/tokens, disposes the first controller to model quit, creates a new controller over the same data directory, and verifies the signed-in session is restored without clearing tokens.
- [x] Sign out clears the session and returns to setup.
  - Evidence: `SignOutCommand_ClearsSensitiveSetupState` verifies session state, secrets, settings, and commands are cleared after sign-out.
- [x] Sign in again works without reinstalling.
  - Evidence: `SignOutThenSignInAgain_ReusesSameInstallationFlow` signs out and signs back in in the same app installation flow.

## Logoff/Logon Gate

- [x] With autostart enabled and signed in, log out of Windows.
  - Evidence: manual lifecycle smoke used a full Windows reboot instead of simple logoff, which is a stronger autostart/session-restoration path; stdout and local observations are summarized in [cotton-logon-smoke-20260615-225231.log](private-evidence:cotton-logon-smoke-20260615-225231.log).
- [x] Log back in.
  - Evidence: after Windows login, Cotton Sync process `25048` was running from `<installed Cotton Sync executable>` with command line `--start-minimized`.
- [x] Cotton Sync starts automatically.
  - Evidence: HKCU Run value `Cotton Sync` matched `"<installed Cotton Sync executable>" --start-minimized`; after reboot the process started automatically at `2026-06-15T22:51:29-07:00`.
- [x] The window does not steal focus unless explicitly launched.
  - Evidence: tester reported the window appeared briefly at the bottom of the screen and immediately hid to the tray after reboot; no persistent foreground window remained.
- [x] Tray icon is present.
  - Evidence: tester reported Cotton Sync hid to the tray after reboot.
- [x] Signed-in session is restored.
  - Evidence: post-reboot log shows `GET /api/v1/auth/me` completed with status `200` at `2026-06-15T22:51:34-07:00` without manual re-authentication.
- [x] Existing Desktop sync pair is still configured.
  - Evidence: manual smoke used the safe local sync root `<isolated logon-smoke root>` and remote folder `/CodexSyncQa/logon-smoke-20260615-224607`; post-reboot log shows sync pair `aa13d8bb-92aa-46ea-9fd6-f99cc4d9340a` started and completed a sync pass with `0 activities`.
- [x] Creating a new Desktop file after logon uploads without re-authentication.
  - Evidence: after reboot, creating `<isolated logon-smoke root>\post-logon-upload-20260615-225231.txt` triggered `Requesting local-change sync` and completed a sync pass for pair `aa13d8bb-92aa-46ea-9fd6-f99cc4d9340a` with `1 activities` at `2026-06-15T22:52:33-07:00`; no browser approval or sign-in retry was required.

## Diagnostics

- [x] Diagnostics export succeeds while signed in.
  - Evidence: `ExportDiagnosticsCommand_AddsStatusAndRecentActivity` verifies a signed-in app can export diagnostics and expose the bundle path.
- [x] Diagnostics export contains the published app version.
  - Evidence: GitHub Actions run `27592560981`, job `Desktop Windows Package Smoke`, steps `Smoke desktop Windows zip archive`, `Smoke desktop Windows installer`, and `Smoke desktop Windows installer upgrade`, completed successfully with `smoke-diagnostics-export.ps1 -ExpectedAppVersion 0.0.1`, matching that release's manifest version.
- [x] Diagnostics export does not contain raw access tokens, refresh tokens, passwords, or TOTP values.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore` passed 516/516.
- [x] Logs contain no unhandled exception or crash-on-start stack trace.
  - Evidence: local isolated self-test via `dotnet src\Cotton.Sync.Desktop\bin\Debug\net10.0\Cotton.Sync.Desktop.dll --self-test --data-dir <temp>` exited 0; fresh `cotton-sync.log` length 113 contained no `unhandled`, `crash-on-start`, `fatal`, `stack trace`, or `exception` pattern.
- [x] Desktop self-test includes a writable update-cache check.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~Updates|FullyQualifiedName~DesktopShellControllerSelfTestTests"` passed 47/47.

## Auto Update

Decision: first-release updates stay installer-based. The app checks the release manifest on startup, may download a SHA-256-verified installer in the background, may start the release installer silently after a user clicks `Update`, and a downloaded update may also be applied silently on the next app start. No custom DLL replacement script is in release scope.

- [x] Release manifest parsing, semantic version comparison, Windows installer asset selection, update-cache download, SHA-256 verification, and bounded transient retry are covered by focused unit tests.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~Updates|FullyQualifiedName~DesktopShellControllerSelfTestTests"` passed 47/47.
- [x] Silent installer arguments and installer relaunch hook are covered by focused tests.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~Updates|FullyQualifiedName~DesktopPackagingMetadataTests"` passed 57/57.
- [x] App exposes a desktop UI path to check the release manifest without blocking sync commands.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore -p:UseSharedCompilation=false` passed 641/641.
- [x] App shows a readable update-available and update-ready state.
  - Evidence: same desktop test suite passed 641/641.
- [x] App performs a startup update check/download without blocking sync commands, and a startup update failure stays retryable without replacing the main sync status.
  - Evidence: `ShellViewModelSyncPairCommandTests.InitializeAsync_AutoDownloadsUpdateOnStartupWithoutBlockingSyncCommands` and `ShellViewModelSyncPairCommandTests.InitializeAsync_WhenStartupUpdateFailsShowsRetryableStatusWithoutOverridingSyncStatus` passed in the focused update run `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore --filter "FullyQualifiedName~Update|FullyQualifiedName~DesktopPendingUpdateStartup|FullyQualifiedName~DesktopShellControllerSelfTestTests" -p:UseSharedCompilation=false` (69/69), and the full desktop suite passed 641/641.
- [x] App can launch the downloaded, SHA-256-verified Windows installer update flow in silent relaunch mode.
  - Evidence: same desktop test suite passed 641/641.
- [x] Downloaded update can be deferred and applied automatically on the next app start before opening the main UI.
  - Evidence: same desktop test suite passed 641/641.
- [x] Update install from an older installed version to a newer GitHub release is verified.
  - Evidence: GitHub Actions run `27652173622`, job `Desktop GitHub Release Upgrade Smoke`, completed successfully after `Publish Sync Client Release` published `v0.0.4`; the smoke downloaded the GitHub release Windows installer, upgraded from the older test package, verified the installed product/diagnostics version, and uninstalled cleanly. Earlier baseline: run `27593795457` completed the same flow against the first `sync-client-latest` release.
- [x] Offline/firewall/404/hash-mismatch updater failures are shown as readable, retryable status.
  - Evidence: `dotnet test src\Cotton.Sync.Desktop.Tests\Cotton.Sync.Desktop.Tests.csproj --no-restore -p:UseSharedCompilation=false` passed 641/641.

## Uninstall

- [x] Uninstall completes successfully.
  - Evidence: GitHub Actions run `27591853188`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed silent uninstall with exit code 0.
- [x] Install directory is removed or only expected logs remain.
  - Evidence: GitHub Actions run `27592560981`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, completed successfully and fails if the install directory remains non-empty after uninstall or after reinstall cleanup.
- [x] Start Menu entry is removed.
  - Evidence: same Windows installer smoke fails if `Cotton Sync.lnk` or `Uninstall Cotton Sync.lnk` remains after uninstall.
- [x] Autostart Run key `Cotton Sync` is removed.
  - Evidence: same Windows installer smoke fails if HKCU Run value `Cotton Sync` remains after uninstall.
- [x] Reinstall after uninstall works.
  - Evidence: GitHub Actions run `27592560981`, job `Desktop Windows Package Smoke`, step `Smoke desktop Windows installer`, reinstalls the same release installer after uninstall, verifies `Cotton.Sync.Desktop.exe`, runs `--self-test`, and uninstalls cleanly again.

## Report Back

Send back:

- Windows edition/build.
- Whether this was a separate machine, VM, or clean user profile.
- Release commit and version.
- Any failed checkbox text.
- Screenshot or exact text for every error/prompt.
- Whether firewall prompts appeared and which action was retried after approval.

## Automation Limitations

- Manual installed-release GUI and reboot/logon smoke closed the release checklist on this host. Computer Use desktop automation is still unavailable in this Codex session: bootstrap fails with `Package subpath './dist/project/cua/sky_js/src/targets/windows/internal/computer_use_client_base.js' is not defined by "exports" in <local Codex runtime path>`. Future fully automated tray/logon coverage needs a working Computer Use helper or a dedicated Windows UI harness, but this is no longer a release blocker for this smoke checklist.

## Supplemental Desktop Live Smoke

- PASS: desktop controller full-mirror CRUD smoke converges against live `<verified Cotton Cloud server>`.
  - Evidence: `dotnet src\Cotton.Sync.Desktop\bin\Debug\net10.0\Cotton.Sync.Desktop.dll --desktop-live-sync-smoke --server <verified Cotton Cloud server> --remote-path /CodexSyncQa/desktop-live-20260616-052345-481cda88 --local-root <temp-client-a> --second-local-root <temp-client-b> --data-dir <temp-data>` completed with `Failures: 0`, `Converged: yes`, and `Final state entries: 0`; stdout is saved in [cotton-desktop-live-smoke-20260616-052345-481cda88.log](private-evidence:cotton-desktop-live-smoke-20260616-052345-481cda88.log).
  - Observation: local create propagation required 12 bounded poll attempts on the slow public staging instance; the test still converged without user re-authentication or manual retry. Keep this as performance/UX watch evidence for release notes and future tuning.
  - Scope note: this closes the first-sync-pair correctness checks through the desktop controller stack, not the tray/logoff/logon checks that require an actual Windows shell session.
- PASS: separate wife-computer installed diagnostics show the released full-mirror desktop client signed in and idle on a real Desktop pair.
  - Evidence: user-provided diagnostics bundle `<private diagnostics bundle>` was extracted locally to `private-evidence:wife-clean-windows-smoke-20260617-055008/`. `diagnostics.json` reports app `0.0.4`, server `<redacted server profile>`, signed-in account `<redacted clean-machine account>`, `Desktop` pair `<clean-machine Desktop path>` -> `/Desktop`, mode `fullMirror`, status `Idle`, change cursor `6728`, last sync `2026-06-17T05:36:06Z`, autostart enabled, DPAPI token storage verified, file watcher OK, notification adapter OK, and all self-test items passed. Logs show one earlier expired browser sign-in request and transient `401 -> token refresh succeeded` retry warnings, but no persistent crash/fatal/error after the successful session.
  - Scope note: this is additional real-machine evidence for the normal full-mirror release. It does not close any Windows virtual-files checkbox because the configured pair mode is `fullMirror`.
- TODO: repeat this installed-artifact smoke with `--sync-mode windows-virtual-files` after the VFS release artifact is published. That run is expected to create the same two-client desktop-controller scenario in virtual-files mode and feed evidence into `docs/windows-virtual-files-release-checklist.md`; it does not replace the separate Explorer/tray/logon checks.

## Supplemental CLI Live Smoke

This section is release evidence for the console client and shared full-mirror sync core. The clean-Windows desktop GUI/reboot checks are closed separately by the installed-release evidence above.

- PASS: CLI browser-login can be approved through `<verified Cotton Cloud server>` and revoked after use.
  - Evidence: `dotnet src\Cotton.Sync.Cli\bin\Debug\net10.0\Cotton.Sync.Cli.dll auth-browser --server <verified Cotton Cloud server> --timeout-seconds 8 --device-name "Cotton Sync CLI QA"` printed an approval URL. Chrome approval showed `Access granted`; a longer run completed with `Signed in: <redacted account username>` and `Signed out.`.
- PASS: CLI two-client full-mirror smoke converges against live `<verified Cotton Cloud server>`.
  - Evidence: `sync-soak --browser-login --remote-path /CodexSyncQa/20260616-044653-c5fe98ce --iterations 1 --probe-file probe.txt --second-local-root <temp-client-b>` completed with `Sync errors: 0`, `Final convergence activities: 0`, `Converged: yes`, and `Failures: 0`. State summaries reported `Entries: 1` for both sync pairs, and `probe.txt` SHA-256 matched on both local roots: `893644e59dd0494ec01a5ae2e88490199b5425efd9b3d221657cb6cdbc4e1fdb`.
- PASS: CLI two-client full-mirror CRUD smoke converges against live `<verified Cotton Cloud server>`.
  - Evidence: `sync-crud-smoke --browser-login --remote-path /CodexSyncQa/crud-20260615-215709-5f61defb --local-root <temp-client-a> --second-local-root <temp-client-b>` completed with `Failures: 0`, `Converged: yes`, `Final convergence activities: 0`, and `Final state entries: 0`; stdout is saved in [cotton-cli-crud-smoke-20260615-215709-5f61defb.log](private-evidence:cotton-cli-crud-smoke-20260615-215709-5f61defb.log). The run verified initial idle, client A create -> client B download, client B create -> client A download, client A rename -> client B rename, client B rename -> client A rename, client A delete -> client B delete, and client B delete -> client A delete.
  - Scope note: this is live evidence for the console client and the shared full-mirror sync core. The clean-Windows desktop GUI/reboot checks are closed separately by the installed-release evidence above.
