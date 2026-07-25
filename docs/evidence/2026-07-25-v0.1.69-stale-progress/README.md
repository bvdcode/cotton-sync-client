# Cotton Sync v0.1.69 installed Explorer evidence

## Provenance

- Commit: `935e622a93685e9f0fc850f3637270a595f26f82`
- GitHub Actions run: `30177260018`
- Release: `v0.1.69`
- Installer SHA-256: `E99C95B7181E34B87BFCCF7917FA21785281BD8EF06207E2AD1E3411089C4E7E`
- Installed product version: `0.1.69+935e622a93685e9f0fc850f3637270a595f26f82`
- Installed executable SHA-256: `9EECDAC3AE690E9863E8D3E242AD7D29584778ACCE7052A81332B4D25EE1E32F`

## Scenario

The exact installer-built application ran the production Windows Cloud Files
runner against an isolated QA root. Initial placeholder population paused after
creating the early subtree, then the real Explorer `Always keep on this device`
verb was invoked on the ancestor. Population resumed and created the late nested
subtree.

The run proved that both early and late descendants became pinned and hydrated,
unpinning did not remove materialized content, and repinning did not trigger
another download. The sync root was unregistered and the runner ended with
`Result: passed`; stderr is empty.

This is installed native Explorer evidence. The later real-demo run below
repeats the scenario with a large server-backed initial sync.

## Installed hydration transition

The exact installed artifact also ran the deterministic hydration-progress
state machine through the real desktop UI. One run captured 80 rendered frames
over 22 seconds. A second, frame-capture-free run sampled UI Automation 212
times over 22 seconds, or 9.64 samples per second.

All 50 expected download names were observed. After the initial warm-up, no
download name remained visible longer than 302 ms. The UI returned to aggregate
`Making files available` state in 51 between-file samples. After completion, 29
samples reported `Connected` with no download name, aggregate progress, or
rate text.

Active file transfer, with aggregate progress above and the concrete file below:

![Active hydration shows distinct global and file progress](hydration-active-specific-file.png)

Between files, the row returns to aggregate population progress:

![Completed transfer restores aggregate progress](hydration-between-files-aggregate.png)

After completion, stale transfer text and rate are gone:

![Completed hydration returns to Connected](hydration-complete-connected.png)

The machine-readable [transition report](hydration-transition-report.json),
[212 high-frequency samples](hydration-transition-samples.json),
[transition groups](hydration-transition-groups.json), and
[SHA-256 manifest for all 80 rendered frames](hydration-frame-manifest.json)
preserve the full result. Only the three representative frames are retained;
the temporary raw frame set was removed after its hashes and observed UI state
were recorded.

This deterministic installed run exercises the same transfer-completion and
aggregate-progress UI pipeline as the reported stale label. The real-demo run
below independently exercises the same UI with network transfers.

## Real demo initial sync

The exact installed release used an isolated authenticated profile against
`https://app.cottoncloud.dev/`. It populated 2,504 files and 51 directories,
with 77,348,864 bytes of real remote content. Explorer invoked `Always keep on
this device` once when only 2 files existed locally.

The run finished with all 2,504 files and all 50 descendant directories under
the selected ancestor pinned, zero offline files, zero unpinned files, and
matching SHA-256 for early, late-created, and 16 MiB sentinels. The app returned
to `Connected` without a stale filename, rate, or progress bar.

The live UI visibly separated aggregate progress from current-file progress:
the top panel reported `2487 cloud items ready` while the row reported
`Downloading track-2484.bin`. An independent first pass showed
`2 of 2504 files` above `Downloading large-02.bin`.

Full screenshots, 683 timestamped state samples, a SHA-256 manifest for 680
captured frames, public diagnostics, cleanup proof, and scope notes are in the
[real-demo evidence folder](real-demo-initial-sync/README.md).

## Screenshots

Ancestor root after the late subtree appeared:

![Root descendants show always-available status](explorer-always-keep-root.png)

Early descendant file:

![Early descendant shows always-available status](explorer-always-keep-early.png)

Late nested descendant file:

![Late nested descendant shows always-available status](explorer-always-keep-late-nested.png)

## Machine evidence

- `always-keep-descendant-attributes.json` records `pinned=true`,
  `offline=false`, and `recallOnDataAccess=false` for the root, directories,
  and files.
- `explorer-always-keep-during-population-runner-temp.stdout.log` contains the
  complete runner transcript with host paths, PID, and session ID normalized.
- The runner stderr capture was empty.
- `installed-self-test.stdout.log` contains the installed self-test transcript
  with host paths normalized.
- The installed self-test stderr capture was empty.
