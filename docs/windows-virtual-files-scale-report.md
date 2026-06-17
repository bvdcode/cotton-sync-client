# Windows Virtual Files Scale Report

This report summarizes the Windows virtual-files work without publishing private
account data, local machine paths, approval URLs, raw diagnostics, or screenshots.

## Scope

The Windows desktop client now has a Cloud Files based sync mode that can expose
remote content as online-only placeholders, hydrate files on demand, dehydrate
local content through Explorer, reconnect after process or shell restarts, and
clean up sync-root registrations when a pair is removed.

## What Changed

- Added Windows Cloud Files adapter coverage for sync-root registration,
  placeholder creation, callback dispatch, hydration, dehydration, transfer
  progress, diagnostics, and pair deletion cleanup.
- Added Windows Storage Provider shell registration so virtual-file roots show
  up through Explorer with first-class shell integration.
- Added streaming remote-tree materialization and resume-state tracking so large
  accounts do not require a full in-memory remote tree before the UI starts
  showing useful files.
- Hardened local watcher handling so provider-originated placeholder churn does
  not trigger recursive local sync work.
- Blocked provider self-implicit hydration and added requester diagnostics for
  Cloud Files fetch callbacks.
- Added installed-artifact live-smoke entry points for sign-in, pair creation,
  existing-local-file preservation, placeholder hydration, reconnect, cleanup,
  and high-scale sampling.
- Updated desktop UI copy and progress behavior so long placeholder creation
  runs report useful state instead of noisy queued-work messages.
- Added CI coverage for desktop package build artifacts and focused Windows
  smoke flows.

## Scale Evidence

The private evidence set was reviewed and reduced to the following publishable
facts:

- A live Windows Cloud Files smoke created 10,000 remote-only placeholders in
  roughly 2.3 seconds, then enumerated the directory in roughly 2 ms.
- Installed-artifact runs against a production-scale account showed early
  top-level placeholder visibility while the full tree was still being streamed.
- Resume-state fixes kept memory bounded during large partial-root resumes while
  state and placeholder counts continued advancing.
- Provider-originated watcher events were observed at high volume and then
  suppressed without converting them into local sync churn.
- Follow-up runs after the self-hydration mitigation showed no unintended
  content downloads, fetch callbacks, or queued local-change work during
  placeholder population.
- Explorer restart, tray quit/reopen, autostart, pair removal, uninstall, and
  sync-root cleanup were covered through installed or live Windows evidence.

## Verification Highlights

- Focused Cloud Files adapter and packaging tests covered sync-root connection,
  storage-provider registration, placeholder identity, callback dispatch,
  hydration coordinator behavior, and cleanup paths.
- Core sync tests covered placeholder-safe upload, conflict preservation, remote
  delete behavior, local baseline handling, and virtual-file resume behavior.
- Desktop ViewModel tests covered compact row rendering, progress text, browser
  sign-in state transitions, session restoration, and action-required messages.
- Installed self-tests verified secure token storage availability, autostart
  registration, server identity, virtual-files capability, and configured sync
  roots.
- CI packaging checks verified Windows installer output and desktop release
  artifact shape.

## Privacy Boundary

Raw local evidence is intentionally not committed here. The private evidence
archive contains machine-specific paths, personal account identifiers, local
cloud filenames, screenshots, installer artifacts, and diagnostic log tails. This
public report keeps the useful engineering facts while avoiding those details.

## Remaining Release Gates

- Re-run the final installed-artifact flow with a clean public QA account.
- Capture redacted screenshots from a synthetic dataset instead of a personal
  account.
- Keep raw diagnostics out of Git history and release assets; publish only
  summarized, sanitized findings.
