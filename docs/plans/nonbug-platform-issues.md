# Non-bug platform issues implementation plan

This plan was written before implementation after comparing `origin/main`, all fetched
remote branches, merged pull requests, issue timelines, and the current worktree.

## #16 Android foreground service and reconnect

- Existing implementation: `LinkGalleryForegroundService` already owns the HTTP,
  NSD, and UDP services; publishes a persistent notification with a stop action;
  uses `START_STICKY`; and refreshes advertising from a default-network callback.
  Pairing credentials already expose desktop names. These pieces came from the
  existing pairing/discovery work and must be reused.
- Gap: callbacks restart advertising immediately and repeatedly, have no bounded
  retry/backoff when Wi-Fi is still settling, and the UI/notification currently
  describes all paired desktops as though they were connected. Lifecycle policy is
  not covered by unit tests.
- Implementation: add a small pure lifecycle/retry policy; debounce network events,
  retry only while no LAN address is available with bounded exponential delays,
  cancel pending work on stop, and distinguish paired from recently active desktop
  sessions in state/notification. Keep stop synchronous with HTTP/discovery teardown.
- Tests: pure retry-policy tests plus existing Android server/discovery/unit tests;
  retain real-device checks for lock screen, Doze, Wi-Fi hand-off, process reclaim,
  notification accuracy, and notification stop.

## #17 Pocket 3 source and edited-export classification

- Existing implementation: protocol, Android `MediaRecord`, desktop `MediaItem`,
  HTTP mapping, and SQLite cache already carry `sourceDevice`, `sourceApplication`,
  and `isEditedExport`; Android MediaStore already exposes filename, album, and
  relative path. Desktop album aggregation and local-copy folder selection already
  exist. GitHub code search did not find an applicable open-source Mimo classifier.
- Gap: Android always leaves the three source fields empty/false. There is no
  conservative evidence model, Pocket 3 query filter, or source-aware copy folder
  helper.
- Implementation: add a pure classifier using combinations of Mimo/DJI directories,
  album, owner package, MIME/codec hints when available, and Pocket filename patterns.
  Require multiple signals for Pocket 3, mark ambiguous files unknown, and mark an
  edit only from explicit export/editor evidence. Flow classification through the
  existing protocol. Add reusable desktop source filter and source/year/month folder
  policy without creating a direct Pocket connection path.
- Tests: table-driven Android classification samples, JSON/client mapping tests,
  desktop filter/folder policy tests, and OpenAPI lint.

## #18 contract and security regression

- Existing implementation: Android tests already cover bearer authentication,
  revoked tokens, malformed IDs/query strings, read-only method rejection, range
  boundaries and streaming truncation. Desktop transfer tests already cover
  interruption, resume, hash mismatch, remote size change, restart recovery, and
  atomic `.partial` publication. CI already runs OpenAPI lint, Android tests, and all
  .NET tests.
- Gap: no executable cross-client schema fixture; no replay protection on mutable
  authenticated status messages; no explicit traversal matrix; CI does not publish
  test reports as artifacts.
- Implementation: extend security matrices, add authenticated/replay-safe transfer
  status contract tests, add a generated non-private contract fixture shared by
  Android/client tests, and upload only test reports (never media, tokens, or keys).
- Tests: Android protocol/server tests, desktop HTTP compatibility and reliable
  transfer tests, OpenAPI lint, and solution test run. Full UI E2E remains a local
  emulator/Windows runner because it needs interactive desktop hardware.

## #82 simplified Android save destination/progress

- Existing implementation: Android already has device/service state UI and an
  authenticated pairing identity; Windows owns transfers and destination paths.
- Gap: no protocol for a paired desktop to publish ephemeral, sanitized transfer
  status, and therefore no Android status model/UI. Sending a full Windows path would
  violate the issue.
- Implementation: add an authenticated, replay-safe status endpoint whose schema
  accepts only desktop-generated task ID, destination display name, item/byte
  progress, state, sequence and expiry. Keep it memory-only, bind desktop identity
  from the token rather than request JSON, clear it on expiry/cancel/offline, and show
  only computer, display name, item count, total progress, and stop-sharing action.
- Tests: authorization, validation, replay/expiry and no-path-leak tests plus Android
  presentation-state tests. Windows integration can post this contract from the
  transfer orchestration work without moving file ownership to Android.

## #95 Android all-photos/albums navigation

- Existing implementation: the redesigned Compose UI already has Photos and Albums
  destinations, two-column album cards, album-detail grid, square thumbnails, real
  bucket IDs/names, and a bounded album-page cache.
- Gap: the issue asks for one gallery-level segmented switch, 3/4/5-column density,
  stable navigation/scroll restoration, and explicit offline cached-index behavior.
  Current tabs are separate bottom-level destinations, density is fixed, and list
  state is not saved.
- Implementation: consolidate Photos/Albums under a gallery destination with a
  segmented switch, hoist/save grid and album navigation state, add density control,
  preserve square thumbnails and two-column album cards, and keep the existing
  bounded cache. Do not add media mutation actions.
- Tests: Compose semantics/navigation tests and pure album grouping tests; real-device
  checks for scroll restoration, process recreation, and offline cached index.

## Excluded: #85

Issue #85 currently has the GitHub `bug` label. The assignment explicitly forbids
handling any bug issue, so this branch will not change it.
