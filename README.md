# Steamphone

**Android activity, connected directly to Steam.**

Steamphone is a native C# / .NET MAUI application that maps installed Android apps to Steam AppIDs. A user-started foreground service detects the active app, announces the mapped game through SteamKit2, and records playtime in a local SQLite database. It runs entirely on the device; there is no application backend or Steam Web API key to configure.

The interface currently uses Romanian. Source code, build tooling, and this contributor guide use English.

## Features

- Steam username/password authentication with Steam Guard codes or mobile approval.
- Persistent login through Android-backed SecureStorage; passwords are never saved.
- Installed-app selector with actual display names and icons, search, and optional system apps.
- Editable app-to-AppID mappings and Online / Away / Busy / Snooze / Offline idle status.
- Independent Public, Invisible, Online with game hidden, and fully Local visibility modes.
- A live friends hub with game activity, search, filters, profiles, and Steam store/library shortcuts.
- Foreground service with a persistent notification, stop action, and reconnect backoff.
- Local playtime history with crash recovery and conservative handling of suspended execution.
- JSON backup import/export for mappings, preferences, and playtime history.
- Light, Dark, and System themes with five navigation tabs and an explicit Steam presence preview.
- Event-driven reconnects, configuration caching, batched history writes, and optional screen-off connection continuity.
- Signed ARM64 and x64 APKs, automatic GitHub releases, and commit-based changelogs.

## Install and use

Open the repository's **Releases** page and download the **arm64** APK for most Android phones. Use **x64** for an x86_64 Android emulator. Android 8.0 / API 26 or newer is required. Releases include checksums and a build manifest.

1. Install the APK and open Steamphone.
2. Connect your Steam account. Enable **Prefer codul Steam Guard** before logging in if you prefer a code over approval in the Steam app.
3. Open **Setări → Deschide accesul la utilizare** and grant usage access to Steamphone.
4. Allow notifications. For better background continuity, review the app's battery optimization settings and any manufacturer-specific autostart restrictions.
5. Open **Mapări**, select an installed app, and enter the Steam AppID to announce.
6. Start monitoring from **Acasă**, then open the mapped app. Return to the launcher or another app to restore the selected idle status.

In **Joc vizibil**, a mapped game uses Online while active. The selected idle status applies after leaving the game. Locking the screen stops local playtime accounting. The notification's **Oprește** action stops the service. Signing out removes the Steam session while retaining mappings and local history.

### Visibility and the friends hub

Choose a mode under **Acasă → Cum te văd prietenii**. It saves automatically, applies during monitoring, and survives restarts and backup transfer.

| Mode | Steamphone presence | Game announcements | Local history | Live friends |
| --- | --- | --- | --- | --- |
| **Joc vizibil** | Online in a mapped game; selected idle status otherwise | Enabled | Yes | Yes |
| **Invizibil** | Steam Invisible (appears offline in the friends UI) | Disabled by default; optional opt-in | Yes | Yes |
| **Doar online** | Online, including during a mapped game | Disabled | Yes | Yes |
| **Doar local** | Disconnects from Steam | Disabled; no Steam connection | Yes | No |

Private modes do not submit the mapped game's AppID by default, including on reconnect. Switching from an announced game to a mode that suppresses announcements clears that announcement before applying the new persona state, provided this client owns the playing session. Unannounced playtime is recorded only on the phone. Existing installations keep their previous public behavior until a different mode is selected.

To preserve the option of Steam hours with an Invisible persona, enable **Setări → Preferințe → Anunță jocul când sunt invizibil** and save. This is off by default. When enabled, Invisible is applied before submitting the game ID, including after reconnect; Valve still decides whether hours are credited. The game is sent to Steam, and profile privacy remains separate. Online-only and Local modes always suppress the game, even with this preference enabled.

These controls apply to Steamphone's connection. Another Steam client, a game on your PC, and your Steam profile's game privacy settings remain independent. Invisible is a friends-list presentation state, not a promise that Valve receives no connection metadata. Use **Doar local** when no Steam connection is wanted.

**Prieteni** shows your real friends sorted by playing, online, then offline. Search by name or game, filter the list, and tap a friend for their profile or game store page. Steam sends updates through the same authenticated connection; no Web API key, repeated web polling, or avatar image downloads are required. Store, library, and profile shortcuts open externally only when tapped. This is a native companion hub, not an injected Shift+Tab overlay or a full chat client.

### Battery and data

The configured 1–10 second interval applies with the phone unlocked. Screen and network broadcasts wake monitoring immediately; locked-screen fallback checks run at most every 30 seconds without scanning usage events. Configuration is reloaded only after an edit. Unchanged presence and notifications are not resent, and reconnect waits for connection events with exponential backoff up to two minutes. No connection attempts run while Android reports no Internet or Local mode is selected.

History accumulates in memory and checkpoints every ten seconds, on app transitions, and on stop. Opening statistics or exporting a backup flushes the last observed time. Abrupt process termination may lose up to one checkpoint interval; suspended gaps over 30 seconds are still excluded.

In **Setări → Preferințe**, **Păstrează conexiunea cu ecranul blocat** is off by default so Android can suspend the CPU. Turn it on to opt into a bounded, renewed wake lock while locked, Internet is available, and a Steam mode is selected. It uses more battery and does not override Doze or manufacturer restrictions. A 5–10 second detection interval further reduces active checks at the cost of slower game transitions.

### Import and export

Open **Setări → Backup și transfer**:

- **Exportă un fișier** opens Android's Save As picker, initially at **Downloads**. Change the filename, folder, SD-card location, or document provider before saving.
- **Importă un fișier** opens the Android file picker. After validation, the app shows the number of mappings and sessions before applying the backup.

A backup contains preferences, app mappings, and checkpointed playtime sessions. It **never contains** passwords, refresh tokens, Steam Guard data, the installation identity, or signing keys. Logging in on another phone remains a separate step.

Imports merge history by session ID. Importing the same file repeatedly does not double playtime, and an older backup does not reduce an existing session's recorded duration. Matching package mappings and preferences take the imported values; unrelated mappings/history remain. Monitoring briefly stops during an import and restarts if it was previously running. Invalid files or conflicting session identities leave the database unchanged.

Backups are UTF-8 JSON with schema version 1, a 32 MB size limit, and at most 100,000 mappings or sessions. Visibility, Invisible game-announcement opt-in, and screen-off connection preferences are included. Older backups without these optional fields import with Public visibility, the opt-in disabled, and battery-friendly screen-off behavior; older app versions may reject backups containing the new fields. A valid empty template is available at [examples/steamphone-backup.json](examples/steamphone-backup.json). Files use Android's Storage Access Framework, so broad storage access is not required.

## Development setup

The supported build environment and GitHub runner are **Windows with PowerShell**. The workflow explicitly uses `windows-2025` and `pwsh`; it does not mix Linux commands with Windows paths.

Install:

| Tool | Requirement |
| --- | --- |
| .NET SDK | 10.0.302, or a newer compatible .NET 10 feature band allowed by `global.json` |
| .NET workload | `maui-android` |
| Android SDK | API 36 and Build Tools 36.0.0 |
| JDK | 21 |
| Node.js | 22 or newer; scripts use built-in modules and require no npm install |
| Git | For source checkout and release changelog generation |

Visual Studio with MAUI support is optional. Android Studio is useful for managing emulators. Android-only builds do not require iOS, Xcode, or Windows App SDK workloads.

### Set up a new contributor machine

1. Clone this repository and open a PowerShell terminal in its root.
2. Install .NET 10 and Node.js, then run:

```powershell
./scripts/setup-android.ps1
```

The setup script installs the MAUI Android workload and the required Android/JDK dependencies. Its default locations are `%LOCALAPPDATA%/Android/Sdk` and `%LOCALAPPDATA%/Microsoft/jdk-21`. Pass `-AndroidSdk` and `-JavaSdk` if you use different locations. The SDK dependency installation accepts the Android SDK licenses.

If Windows reports insufficient permission to install workloads into the shared .NET SDK, run the setup step from an elevated PowerShell terminal.

3. Run the tests:

```powershell
dotnet test tests/Steamphone.Core.Tests/Steamphone.Core.Tests.csproj -c Release
node --test scripts/release.test.cjs scripts/signing.test.cjs
```

4. Build a contributor APK without access to the production key:

```powershell
./scripts/build-android.ps1 -Runtime android-arm64 -DevelopmentSigning

# Emulator build:
./scripts/build-android.ps1 -Runtime android-x64 -DevelopmentSigning
```

The script prints the APK paths under `artifacts/release/<version>/`. Development APK filenames end in `-development.apk`. Open `Steamphone.slnx` in your IDE to work on the app, or continue using the CLI.

When `ANDROID_HOME` and `JAVA_HOME` are set, the build script uses them. Otherwise, it uses the setup script's Windows defaults. To override explicitly:

```powershell
$androidSdk = Read-Host 'Android SDK directory'
$javaSdk = Read-Host 'JDK 21 directory'
./scripts/build-android.ps1 -DevelopmentSigning -AndroidSdk $androidSdk -JavaSdk $javaSdk
```

Debug builds can also be requested with `-Configuration Debug`. SteamKit2 3.5.0 requires reflection metadata, so trimming and AOT are deliberately disabled in Release as well.

Local builds default to `major.minor.0` / versionCode 1. CI supplies its generated version explicitly. For maintainer diagnostics, `-Version` and `-VersionCode` can override local defaults; Android requires an equal or higher versionCode to update an existing installation signed by the same key. Use an emulator for development against an already-installed public release.

## Permanent signing identity

The maintainer's local project root contains:

| File | Purpose | Commit it? |
| --- | --- | --- |
| `steamphone-release.keystore` | Permanent RSA-4096 private signing key | **No** |
| `signing.local.json` | Key alias and passwords for local signing | **No** |
| `signing-certificate.json` | Public SHA-256 fingerprint used to verify builds | **Yes** |

The key alias is `steamphone`. The private files are generated once and excluded by `.gitignore`. Their passwords are not printed by the tooling or placed on the build command line. Production signing uses environment-variable password references.

**Back up both private files together to secure storage outside this checkout.** Keep the password record in a password manager or protected backup. The public fingerprint cannot recover a lost private key.

### Continue releases on another machine

1. Clone the repository and perform the development setup above.
2. Privately copy the original `steamphone-release.keystore` and `signing.local.json` into the new checkout's root, using the same filenames.
3. Verify the restored identity and build:

```powershell
node scripts/signing.cjs verify
./scripts/build-android.ps1 -Runtime android-arm64
```

Verification must match `signing-certificate.json`. The local config uses a relative keystore filename, so it works across drive letters and usernames. Do **not** generate a new key for an existing app. The creation command refuses to replace an existing signing identity.

A newly cloned contributor checkout intentionally lacks private signing files. Contributors should use `-DevelopmentSigning`; they do not need the maintainer's production key to develop or test features.

### Moving from earlier development APKs

Earlier local APKs used an Android debug certificate. Android cannot install the production-signed APK over an installation signed by that debug key. Export a JSON backup from the development build, uninstall it once, install the production APK, and import the backup. Reconnect Steam on the new installation.

If an old build predates backup support, build the current code with `-DevelopmentSigning` **on the original development machine**, install that update first, and export the data before switching certificates. A different machine's debug key will not match the old installation.

## GitHub Actions: first-time setup

The release workflow is [`.github/workflows/release.yml`](.github/workflows/release.yml). It uses current, commit-pinned first-party GitHub actions, .NET from `global.json`, JDK 21, explicit Android SDK/JDK paths, and the same PowerShell build script used locally.

Set this up **before the first push containing the workflow**, so the initial run already has its signing secrets:

1. Create an empty GitHub repository, but do not upload the workflow yet. Keep both private signing files out of Git.
2. Enable GitHub Actions for the repository. The publish job requests `contents: write` to create release tags/assets; an organization policy or tag ruleset must permit that operation.
3. Install/authenticate the GitHub CLI on the maintainer's machine, then configure the four signing secrets:

```powershell
gh auth login
$repository = Read-Host 'GitHub repository (owner/name)'
node scripts/signing.cjs github $repository
```

This helper reads the private local files, verifies their certificate, and sends the secrets through stdin to `gh secret set`. It does not commit, push, print passwords, or put secret values in command-line arguments.

| Actions secret | Content |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64-encoded original release keystore |
| `ANDROID_KEY_ALIAS` | `steamphone` |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_PASSWORD` | Signing-key password |

No personal access token is required by the workflow: publishing uses its scoped `GITHUB_TOKEN`. The local GitHub CLI login is only used for this one-time secret configuration. Missing or incorrect signing secrets produce an explicit setup error rather than a release signed by a different key.

After the secrets are configured, the first push to `main` should include `signing-certificate.json`, `Version.props`, `.github/`, `scripts/`, and the application/tests. The workflow will build and publish automatically. No commit or push is performed by the setup helper.

### Release triggers

| Event | Result |
| --- | --- |
| Push to `main` | Build, test, sign, and publish a new stable release |
| Open, update, or reopen a PR targeting `main`, from the same repository | Build, test, sign, and publish a prerelease |
| Fork or Dependabot PR | Build and test with development signing; upload workflow artifacts; no production secrets or public release |
| Manual workflow run on `main` | Publish a new stable build |
| Manual workflow run on another branch | Build/test artifacts only |

PR titles are free-form; release names are generated as **Steamphone v<version>**. A PR does not need to be renamed to trigger the workflow. The workflow uses `pull_request`, not `pull_request_target`.

### Automatic versioning and unique releases

`Version.props` contains the major/minor version. CI computes:

```text
Android versionCode = github.run_number × 100 + github.run_attempt
Display version     = major.minor.versionCode
Release tag         = v<display-version>
PR suffix           = -pr.<pull-request-number>
```

Examples:

| Run | Attempt | App version | Android versionCode |
| --- | --- | --- | --- |
| 1 | 1 | `1.0.101` | 101 |
| 1 | 2 | `1.0.102` | 102 |
| 2 | 1 | `1.0.201` | 201 |
| 3, PR #7 | 1 | `1.0.301-pr.7` | 301 |

Versions are injected into the APK at build time. CI never commits a version-bump change back to `main`, so there is no release loop or branch-protection conflict. Normal pushes and complete reruns receive distinct version codes/tags. Gaps are intentional.

Keep the workflow file and its run history: deleting/recreating the workflow can reset GitHub's per-workflow run counter. Existing tags/releases are checked before publication and are never overwritten or moved. If a run fails after creating a draft, use **Re-run all jobs** for a fresh version. Attempts 1–99 are supported for one run.

There is deliberately no workflow-level concurrency cancellation: every push is allowed to finish and produce its own release. Only validated, production-signed artifacts can enter the publish job. ARM64 and x64 APKs use the same app version and signing certificate.

### Changelog and release assets

Each release includes:

- ARM64 and x64 production APKs.
- `CHANGELOG.md`, also used as the GitHub release body.
- `manifest.json` with the source commit, Android versionCode, artifact sizes, and SHA-256 hashes.
- `SHA256SUMS.txt` covering the APKs and manifest.

Stable changelogs use commit subjects since the previous published stable ancestor. The first release includes the available commit history. PR changelogs use the PR head commits relative to the base commit. Commit links are included; Markdown control characters in commit subjects are escaped. A release is created as a draft and becomes public only after every asset upload succeeds.

Use descriptive commit subjects such as `feat: Add per-app tracking controls` or `fix: Restore status after screen lock` so the generated notes are useful.

## Architecture

| Location | Responsibility |
| --- | --- |
| `src/Steamphone.Core` | SQLite models/storage, backup schema, monotonic tracking, Steam authentication and presence |
| `src/Steamphone/Platforms/Android` | PackageManager, UsageEvents, foreground service, notifications, Android document picker |
| `src/Steamphone/Services` | Monitoring orchestration, platform contracts, SecureStorage, navigation |
| `src/Steamphone/Views` and `ViewModels` | MAUI Shell, login, dashboard, mappings, statistics, settings |
| `tests/Steamphone.Core.Tests` | Database, tracking, backup, authentication prompt, and Steam protobuf tests |
| `scripts/release.cjs` and `release.test.cjs` | Version planning, commit changelogs, artifact validation, immutable publication tests |
| `scripts/signing.cjs` | One-time key generation, identity checks, CI restore, and secret configuration |

Steam, database, and tracking services are shared within the Android process. UI updates are marshalled to the main thread. Monitoring and reconnect work independently so a network timeout does not stop local statistics. Backups use a consistent SQLite snapshot; imports use one atomic transaction.

## Contributing

Create a feature branch, keep changes focused, and run both test suites before opening a PR. Build at least the architecture you changed; exercise Android-specific changes on an emulator/device. Do not include Steam credentials, keystores, signing configs, build output, database files, or personal backups.

For changes involving SteamKit2, verify the actual stable package's API rather than copying examples from another major version. Keep TCP connection attempts serialized, respect `PlayingBlocked`, and preserve reflection metadata. For tracker/backup changes, preserve session IDs and monotonic accounting and add tests for meaningful data-loss or duplication risks.

Only `README.md` is tracked among Markdown files, as configured in `.gitignore`. Local `CLAUDE.md` and other Markdown notes remain private. Release `CHANGELOG.md` files are generated into ignored build output and attached directly to GitHub releases.

## Operational limits and validation

The implementation is covered by local core/release tests and Android build checks. Existing emulator checks include real installed-app selection, foreground notifications, local timing, screen-off handling, and persistence after process termination. This does not establish a guarantee of Steam account compatibility or uninterrupted background connectivity on every device.

- SteamKit2 announces the Steam game identified by AppID; the displayed Steam name can differ from the Android app's label. Valve controls whether an announcement is accepted and whether server-side playtime is awarded. Local SQLite history is independent.
- Android Doze, OEM battery restrictions, force-stop, and loss of connectivity can suspend CPU/network access. A foreground service and wake lock cannot guarantee an uninterrupted TCP connection. The app reconnects when the system allows it.
- `UsageEvents` is sampled, normally every two seconds. Very short transitions can be missed; multi-window tracks the most recently resumed activity. Unobserved gaps over 30 seconds are omitted conservatively.
- `specialUse` foreground services and `QUERY_ALL_PACKAGES` require separate justification/review for Google Play distribution. GitHub APK distribution does not supply Play approval.

Before a public rollout, validate Steam Guard, token resumption, real In-Game visibility from a second Steam account/device, PC playing-session conflicts, revoked sessions, network recovery, and prolonged screen-off/Doze behavior on a physical phone.

### Primary references

[SteamKit2 3.5.0](https://github.com/SteamRE/SteamKit/releases/tag/3.5.0) · [MAUI Android publishing and signing](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli) · [Android document picker](https://developer.android.com/training/data-storage/shared/documents-files) · [Foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types) · [Doze](https://developer.android.com/training/monitoring-device-state/doze-standby) · [GitHub workflow variables](https://docs.github.com/en/actions/reference/workflows-and-actions/variables)
