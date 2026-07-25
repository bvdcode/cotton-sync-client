# Installed v0.1.69 real demo initial-sync evidence

## Scope

The exact installed release
`0.1.69+935e622a93685e9f0fc850f3637270a595f26f82` used an isolated
profile and local root against `https://app.cottoncloud.dev/`.

The run seeded 2,504 files in 51 directories with 77,348,864 bytes of
content. Explorer invoked `Always keep on this device` once on the `Music`
ancestor when only 2 of 2,504 files existed locally. No retry or second
toggle was used.

## Result

- Initial population completed with 2,504 files and 50 descendant
  directories under `Music`.
- All 2,504 files and all 50 directories finished pinned.
- Final Cloud Files state contained zero offline and zero unpinned files.
- The late sentinel `Music\Album-49\track-2499.bin` did not exist at click
  time, then appeared pinned and hydrated.
- SHA-256 matched for the early file, late file, and a 16 MiB file.
- The app returned to `Connected` with no stale filename, rate, aggregate
  progress, or progress bar.
- The installed log contained no errors, action-required state, or
  `0x80070185`. Its one warning was an unauthorized challenge whose token
  refresh succeeded before retry.

The run sampled Cloud Files state 683 times over 498.48 seconds at 1.37 Hz.
Pin state converged 643 ms after the shell verb. The first post-click sample
was captured before the asynchronous pin change; every sample after
convergence had `PinnedFiles == FileCount`. File count never regressed, and
the bounded offline window peaked at 16 files.

## UI evidence

During live population, the top panel reports aggregate work while the pair
row names the current file:

![Global progress is distinct from the current file](live-global-vs-file-progress.png)

The independent first run also captured aggregate `2 of 2504 files` above a
specific large-file download:

![Independent run confirms global versus file progress](live-independent-first-pass-global-vs-file-progress.png)

The transfer label clears before idle:

![Transfer text is cleared before Connected](live-transfer-cleared-before-connected.png)

The final frame contains only `Connected`:

![Final state has no stale progress](live-final-connected.png)

Explorer selected the late-created sentinel and reported `Always available
on this device`:

![Late descendant is always available](live-explorer-late-file.png)

## Machine evidence

- [Installed run report](installed-live-vfs-report.json)
- [Analysis summary](analysis-summary.json)
- [683 timestamped state samples](live-vfs-samples.csv)
- [SHA-256 manifest for 680 captured frames](live-frame-manifest.json)
- [Seed and server verification report](seed-and-configure-report.json)
- [Sanitized test-data manifest](test-data-manifest.json)
- [Public diagnostics bundle](installed-live-public-diagnostics.zip)

The three frames that failed to capture are represented by empty `AppFrame`
values in the sample CSV. Raw frames were removed after hashing; only the
representative screenshots above remain.

## Cleanup

The demo namespace was read back as 2,504 files and 51 directories before
cleanup, permanently deleted, and verified absent through the expected
not-found response. The Cloud Files root was unregistered. The isolated
local run removed 178,622,497 bytes and the empty QA parent directory. The
user-supplied source diagnostics remained untouched.
