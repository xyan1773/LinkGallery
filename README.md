# LinkGallery

**English** | [简体中文](README.zh-CN.md)

> One gallery. Every device.

LinkGallery is a local-first media browser that turns Windows into one place for viewing and saving
photos and videos from personal devices. The current alpha connects a Windows desktop application to
an Android phone over the local network.

[![CI](https://github.com/xyan1773/LinkGallery/actions/workflows/ci.yml/badge.svg)](https://github.com/xyan1773/LinkGallery/actions/workflows/ci.yml)

## Inspiration

LinkGallery began with a simple frustration: Microsoft Phone Link makes recent Android photos
available on Windows, but it does not feel like a complete gallery for a larger personal media
library.

I wanted to browse a timeline, understand which album and device each item came from, preview media,
and save selected originals to Windows without switching between disconnected tools. I also wanted
the design to grow beyond one phone. Personal media can come from Android, Windows folders, DJI
Pocket cameras, SD cards, external drives, NAS devices, and, eventually, other platforms.

After looking for a project that matched that product idea, I decided to build LinkGallery around one
goal:

> Use Windows as a unified terminal for browsing and managing media from different personal devices.

## What it does today

The current Android-to-Windows prototype supports:

- discovering Android devices on the local network and remembering paired devices;
- reading Android photos, videos, albums, and metadata through MediaStore;
- browsing a Windows timeline with pagination, filters, albums, cached thumbnails, and an offline
  SQLite index;
- previewing photos and playing videos;
- copying selected original files to Windows with persistent jobs, `.partial` files, resume support,
  range validation, and safe final publication;
- recognizing selected DJI Pocket 3 and DJI Mimo media characteristics;
- sharing one versioned OpenAPI contract between the Android and Windows implementations.

The phone remains a **read-only media source**. LinkGallery does not expose operations that delete,
move, rename, edit, or upload media on Android.

## Product principles

- **Local first.** Media browsing and transfer stay on the local network; there is no cloud service.
- **Read-only at the source.** The Android companion only exposes metadata, thumbnails, and original
  read streams.
- **Windows owns file output.** Destination paths, duplicate handling, transfer state, and final file
  publication are controlled by the desktop application.
- **Failures must be recoverable.** Interrupted transfers resume from temporary files and must never
  leave a damaged file that looks complete.
- **Device-specific behavior stays behind adapters.** The gallery should not need to be rebuilt for
  every future media source.

## Design preview

The repository includes an early product and component exploration. It uses synthetic placeholder
content rather than a real personal photo library.

![LinkGallery design-system and product exploration](figma-linkgallery-preview.png)

## Architecture

```text
Windows WPF application
  ├─ timeline, albums, previews, and transfer UI
  ├─ SQLite media index and thumbnail cache
  ├─ device discovery and paired-device storage
  └─ reliable copy and resume coordination
                 │
        local HTTP API + Range
                 │
Android companion
  ├─ MediaStore metadata and content streams
  ├─ authenticated read-only HTTP routes
  ├─ pairing and local credential storage
  └─ NSD/mDNS and UDP discovery
```

The desktop code follows a layered structure:

```text
Desktop / Infrastructure → Application → Domain
```

The Android application separates media access, discovery, pairing, server, identity, and UI code so
that platform behavior does not leak into the shared protocol.

## Key engineering decisions

These decisions were made at specific points in the project rather than being added after the code
was written:

| Decision point | Question | Decision and reason |
| --- | --- | --- |
| Product boundary | Should the desktop be allowed to organize files on the phone? | Keep Android strictly read-only. A gallery bug must never delete or rewrite the source library. |
| Cross-platform contract | How do C# and Kotlin avoid silently drifting apart? | Treat `protocol/openapi.yaml` and shared JSON fixtures as the source of truth, then test both implementations against them. |
| Reliable saving | Who owns paths, duplicate names, and interrupted files? | Make Windows own all output and use persistent jobs, `.partial` files, Range validation, and final publication. |
| Future sources | How can phones, cameras, folders, and NAS devices share one gallery? | Put device-specific behavior behind discovery, media-source, and transfer interfaces. |
| Release readiness | Is a working local prototype safe to present as production software? | No. Security and release reviews led to an explicit Alpha label and a hardening roadmap before wider use. |

## Repository layout

```text
LinkGallery/
├── desktop/          # C#, .NET 8, and WPF desktop application
├── android/          # Kotlin and Jetpack Compose companion application
├── protocol/         # OpenAPI contract and cross-platform fixtures
├── e2e/              # End-to-end acceptance harness
├── docs/             # Architecture decisions, scope, testing, and roadmap
├── scripts/          # Reproducible build and test entry points
└── website/          # Static project website
```

## Technology

| Area | Stack |
| --- | --- |
| Windows | C#, .NET 8, WPF, SQLite |
| Android | Kotlin, Jetpack Compose, MediaStore, Android foreground service |
| Connectivity | Android NSD/mDNS, UDP discovery, HTTP/1.1, bearer authentication, HTTP Range |
| Contract | OpenAPI, shared JSON fixtures, Redocly |
| Quality | MSTest, JUnit, Android UI tests, end-to-end tests, GitHub Actions, Dependabot |

## Quick start

### Prerequisites

- Windows with PowerShell;
- .NET 8 SDK (`global.json` currently requests `8.0.422` and allows the latest 8.0 patch);
- Android Studio with Android SDK 36, platform tools/ADB, and JDK 21;
- an Android 10+ device, or an Android emulator;
- a trusted local network when using a physical phone.

The environment helper recognizes standard `DOTNET_ROOT`, `JAVA_HOME`, `ANDROID_HOME`, and
`ANDROID_SDK_ROOT` variables. See [development setup](docs/development.md) if the tools are not found
automatically.

### 1. Clone and validate the project

```powershell
git clone https://github.com/xyan1773/LinkGallery.git
cd LinkGallery
.\scripts\build.ps1
```

This restores, builds, and tests the .NET solution, then builds the Android debug APK and runs its
unit tests. Use `-SkipDesktop`, `-SkipAndroid`, or `-Configuration Release` when working on one side
only.

### 2. Install the Android companion

Connect a device with USB debugging enabled, then install the APK produced by the build:

```powershell
adb devices
adb install -r android\app\build\outputs\apk\debug\app-debug.apk
```

Open LinkGallery on Android, grant read-only photo/video access and notification permission, and
leave the media service running. For an emulator, forward the service port before connecting:

```powershell
adb forward tcp:39570 tcp:39570
```

### 3. Start the Windows application

```powershell
dotnet run --project desktop\LinkGallery.Desktop\LinkGallery.Desktop.csproj
```

On Android, open the two-minute pairing window. On Windows, choose **Find devices** or **Pair
device**, enter the address code shown by Android, then select the phone to browse its timeline and
albums. If discovery is blocked, allow LinkGallery through Windows Firewall on private networks and
follow the [connectivity guide](docs/connectivity-testing.md).

### Optional reproducible demo data

Normal use reads the phone's existing MediaStore library, so example data is not required. For a
privacy-safe, repeatable emulator demo, place at least one non-sensitive `.JPG` and `.MP4` in a
separate directory and run:

```powershell
.\scripts\run-e2e.ps1 -Profile Smoke `
  -SourceMediaRoot C:\path\to\safe-demo-media
```

The Smoke profile selects the smallest JPG and MP4, copies them only to the dedicated
`/sdcard/DCIM/LinkGalleryE2E` directory, runs the Android/API/Windows journey, and writes evidence to
`artifacts/e2e/<timestamp>-smoke`. The source directory remains read-only. If an appropriate emulator
does not exist, see [end-to-end testing](docs/e2e-testing.md) before using `-RecreateAvd`; that option
recreates the named AVD and has substantial disk requirements.

## Alpha status and security

LinkGallery currently reads private photos and videos, so security takes priority over convenience.
This repository is an **early alpha prototype**, not a production-ready release. Use it only on a
trusted local network and with test or non-sensitive media while encrypted transport, production
pairing hardening, credential lifecycle, dependency updates, privacy-safe demos, and signed release
packaging are completed.

The API intentionally contains no Android media-write routes, and private media routes require a
paired credential. Security regression tests cover authentication failures, revoked credentials,
pairing expiry, path traversal, invalid range requests, and transfer publication boundaries.

Please do not disclose a security issue in a public GitHub issue. Follow [SECURITY.md](SECURITY.md)
for the reporting policy.

## How GPT-5.6 and Codex accelerated the workflow

GPT-5.6 and Codex had different, complementary roles. **GPT-5.6** was used for product reasoning,
trade-off analysis, threat modeling, and challenging assumptions before implementation. **Codex**
acted as the repository-aware engineering agent: it inspected the existing code and Git history,
edited C# and Kotlin together, ran builds and tests, compared the implementation with the OpenAPI
contract, and prepared reviewable GitHub changes.

The working loop was:

1. describe the user journey and acceptance boundary;
2. use GPT-5.6 to compare designs and expose missing failure or security cases;
3. let Codex trace the affected code across Android, Windows, protocol, tests, and documentation;
4. implement the smallest coherent vertical slice;
5. run targeted tests, the full build, and CI, then review the diff before merging.

Concrete examples:

| Stage | Specific GPT-5.6 / Codex use | Decision or artifact | How it accelerated the work |
| --- | --- | --- | --- |
| Product planning | Converted the original Phone Link frustration into user journeys, exclusions, milestones, and GitHub-sized tasks. | `docs/product-scope.md`, roadmap, and focused issues | Replaced an open-ended prototype with testable vertical slices. |
| Architecture | Compared a device-specific UI with a shared media model and adapter boundaries. Codex then traced dependencies across the solution. | Domain/Application/Infrastructure separation and media-source interfaces | Allowed later device sources to reuse the gallery instead of duplicating it. |
| Cross-platform protocol | Reviewed `protocol/openapi.yaml`, Kotlin models, and C# DTOs in one repository-wide pass; added shared fixtures and contract tests. | One OpenAPI boundary consumed by both platforms | Turned two manual review passes into one repeatable compatibility check. |
| Reliable transfer | Enumerated disconnect, retry, disk, permission, duplicate-name, truncated-response, and restart cases before implementation. | Persistent jobs, `.partial` files, Range checks, and safe publication tests | Found failure cases before they became UI bugs or corrupted output. |
| Security and privacy | Scanned tracked files and Git history, reviewed pairing and token flows, checked dependencies and release artifacts, and inspected demo screenshots. | Alpha warning, privacy-safe preview, hardening priorities, and security regression matrix | Prevented personal media from being published and moved security review earlier in the release cycle. |
| Delivery | Ran repository checks, kept unrelated worktree changes out of commits, prepared bilingual documentation, pushed branches, and opened PRs. | Small auditable commits with CI evidence | Reduced context switching between implementation, validation, and GitHub handoff. |

The acceleration was not just faster code generation. The main gain was shortening the feedback loop
between a decision, its cross-platform implementation, its tests, and its review evidence. No
time-saving number is claimed because it was not measured; the repository artifacts above are the
evidence of where AI changed the workflow.

## Challenges and lessons

Keeping two applications consistent is more difficult than building either interface alone. Android
and Windows must agree on media identity, pagination, connection state, authentication, range
behavior, and failure semantics.

Reliable saving is also more than downloading bytes. It requires persistent jobs, safe destination
planning, duplicate handling, retries, disk and permission error classification, interruption
recovery, and a final publication step that never overwrites a valid file accidentally.

The project has reinforced that cross-device software is simultaneously a UX, protocol, networking,
mobile-lifecycle, storage-integrity, privacy, and release-engineering problem.

## What's next

The next priorities are:

1. harden pairing and add encrypted, authenticated transport;
2. finish production-grade credential rotation and release signing;
3. complete the save-to-computer experience and its failure recovery UI;
4. improve background reconnection and physical-device acceptance testing;
5. expand performance coverage for large media libraries;
6. add new media-source adapters after the Android-to-Windows path is stable.

iPhone, public-internet access, cloud sync, face recognition, AI search, Android media editing, and
direct Windows-to-Pocket-3 access are outside the current MVP.

## Documentation

- [Product scope](docs/product-scope.md)
- [Architecture](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [Frontend contract](docs/frontend-contract.md)
- [Security regression matrix](docs/security-regression.md)
- [Contributing guide](CONTRIBUTING.md)

---

**One gallery. Every device.**
