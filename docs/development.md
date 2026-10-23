# Development

This guide owns build, run, test, troubleshooting, and release checks. Run all
commands from the repository root.

## Restore, build, and test

```powershell
dotnet restore Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
dotnet test Manager/Manager.sln --configuration Release
```

Build individual projects when isolating a failure:

```powershell
dotnet build Manager/GameSaves.Core/GameSaves.Core.csproj --configuration Release
dotnet build Manager/GameSaves.Infrastructure/GameSaves.Infrastructure.csproj --configuration Release
dotnet build Manager/GameSaves.App/GameSaves.App.csproj --configuration Release
dotnet build Manager/GameSaves/GameSaves.csproj --configuration Release
dotnet build Manager/GameSaves.Reviewer/GameSaves.Reviewer.csproj --configuration Release
dotnet build Manager/GameSaves.Tests/GameSaves.Tests.csproj --configuration Release
dotnet build Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj --configuration Release
dotnet build Manager/GameSaves.UiMaterialCapture/GameSaves.UiMaterialCapture.csproj --configuration Release
```

Use xUnit filters for feedback, then run the complete suite before submission:

```powershell
dotnet test Manager/GameSaves.Tests/GameSaves.Tests.csproj --filter "FullyQualifiedName~GoogleDrive"
dotnet test Manager/GameSaves.Tests/GameSaves.Tests.csproj --filter "FullyQualifiedName~Sync"
```

## Task documentation and continuation

Use `/ponytail` (full) for the smallest complete implementation and `/caveman`
for concise chat updates. Read their skill instructions before starting. Keep
persisted documentation in complete prose and retain all technical evidence.

After each task, update the affected owner guides and `ROADMAP.md`. Save an
implementation report in `docs/raports/` covering behavior, changed files,
decisions, safety and compatibility effects, exact verification commands and
results, skipped checks, known limits, and unfinished work. Separate inherited
changes from new work and historical test claims from fresh results.

Select the next unfinished task from the reconciled roadmap and create two
Markdown files at the repository root:

- `<TASK-ID>.md`: the task brief, dependencies, scope, acceptance criteria,
  code/documentation references, and verification requirements.
- `prompt-<TASK-ID>.md`: a self-contained execution prompt with current
  repository state, committed/uncommitted work, `/ponytail` and `/caveman`,
  implementation requirements, tests, documentation, and handoff instructions.

For example, the handoff after completing BACKUP-008 uses `BACKUP-009.md` and
`prompt-BACKUP-009.md`. Link both actual files from the implementation report
and this directory's `README.md`; verify the links and record why that task is
next. If work remains incomplete, hand off the unfinished task instead.

End every continuation prompt with the requirement to document its work and
create the following task brief and prompt as two separate Markdown files,
using the same naming and documentation-linking convention. Preserve older
handoff files; do not invent completion evidence or silently skip prerequisites.

## Run tools

```powershell
dotnet run --project Manager/GameSaves.App/GameSaves.App.csproj
dotnet run --project Manager/GameSaves/GameSaves.csproj -- help
dotnet run --project Manager/GameSaves.Reviewer/GameSaves.Reviewer.csproj -- "$env:LOCALAPPDATA\GameSave\gamesave.db"
```

The App and Reviewer require a desktop display. Steam discovery and DPAPI
verification require Windows. Google Drive, SFTP, Steam catalog, and
PCGamingWiki commands may require network access and explicitly authorized
credentials or services. The default regression suite must not.

## UI capture

The headless capture tool writes PNGs and reports to the requested output folder:

```powershell
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-captures
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-layout layout
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-rail rail
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-material material
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-sync-states sync-states
```

The default mode captures tabs, themes, widths, accents, and representative
workspace states. `layout`, `rail`, `material`, and `sync-states` run focused
acceptance sweeps.

`sync-states` is the Sync state matrix: one plan row per presence
(local only, remote only, identical, conflicting, incomplete or unverifiable,
and deselected) and one execution row per transfer and verification state,
captured in both themes, under every accent, and in High Contrast. The rows are
literals pushed straight into the bound collections, so the sweep needs no
provider, account, network, or disk, and no path or account name from the
machine can reach a capture. Alongside the PNGs it writes
`sync-state-report.tsv`, which records each row's label, severity, selection
state, and accessible name. Two rows with identical labels in that report mean
two states have become indistinguishable in words, which is the regression the
sweep exists to catch: colour alone must never be what separates them. Material mode verifies app-owned opacity and navigation semantics and
writes `material-report.tsv`. It simulates accepted composition because the
headless renderer cannot reproduce or report the Windows Acrylic/Mica backdrop;
its `actual` column therefore says `headless-unavailable` rather than claiming
platform support. The `system` theme has no OS theme to inherit there, so
variant-scoped tokens read `unresolved` on those rows; the interactive run
below covers them.

## Window material matrix

Validate the full cross-product before release:

| Dimension | Cases |
| --- | --- |
| Material | None, Acrylic, Mica |
| Theme | Dark, Light, System |
| Accent | Indigo, Teal, Rose, Amber, Violet |
| Desktop background | White, black, representative desktop |
| Window | Main, detached tab |
| Rail | Left, right, top; expanded and collapsed |
| Contrast | Normal, High Contrast |
| Composition | Requested level accepted, request denied or substituted |

The headless sweep above covers app-owned semantics. The rows that depend on
the Windows compositor are covered by the interactive harness, which runs the
real application on the real Win32 platform and reads every number back from
the composited screen:

```powershell
dotnet run --project Manager/GameSaves.UiMaterialCapture/GameSaves.UiMaterialCapture.csproj -- artifacts/ui-material-windows
```

It takes over the screen for a few minutes: it shows a full-screen white and
then black window behind the application, drives the whole matrix live in one
process (no restart), and writes one PNG per row plus
`windows-material-report.tsv`. Its database, settings file, and Steam locator
are throwaway, so no real user data can reach a capture.

Exit codes:

| Code | Meaning |
| ---: | --- |
| `0` | Every requested material was granted and visible, and navigation stayed opaque |
| `1` | Application regression: a `fail-` row, such as navigation letting the background through |
| `2` | Windows substituted or denied a requested level; the run records the fallback |
| `3` | The run did not complete |

Each row records requested versus effective transparency, the page and
navigation brush alpha, and four measurements: rail, Settings strip, and page
difference between the white and black backdrop, plus the page difference
against the same layout with material `none`. An opaque surface measures `0`;
the `none` comparison is what proves Mica, which samples the desktop wallpaper
rather than the window underneath.

To compare against a different revision, add a worktree, copy
`Manager/GameSaves.UiMaterialCapture` into it, and run the harness there. Do
not reset or revert the working tree.

Two checks still need a person, because no measurement replaces them: whether
the material looks right, and the readability of context menus, submenus,
tooltips, and the layout-recovery menu, which are separate top-level surfaces
painted from `NavigationSurfaceBrush`.

Store approved screenshots and the observed-value report under
`artifacts/ui-material-windows/`, never in production assets. The recorded
source comparison and Windows evidence are in the
[material baseline](material-regression-baseline.md).

## Safe cleanup

Use MSBuild cleanup instead of deleting broad output trees:

```powershell
dotnet clean Manager/Manager.sln
```

Do not delete user databases, backup folders, local OAuth configuration, or
untracked files as a build-cleanup step.

## Troubleshooting

- Confirm the .NET 10 SDK with `dotnet --info`.
- Restore before diagnosing missing package or generated Avalonia files.
- If Avalonia reports access to
  `%LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log`, rerun in a normal
  user shell that can write that location. Treat the managed-environment access
  failure as environmental until reproduced there.
- Use `GameSaves -- help` for exact CLI syntax; do not copy old command lists from issues.
- Use temporary data and sanitized paths when reproducing save, archive, or provider failures.

## Pre-release and release checks

```powershell
dotnet restore Manager/Manager.sln
dotnet test Manager/Manager.sln --configuration Release
dotnet build Manager/Manager.sln --configuration Release --no-incremental
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
dotnet list Manager/Manager.sln package --deprecated
git diff --check
git status --short
```

Also run affected UI and provider scenarios on the required platform. Record
warnings, failures, skipped checks, credential/network needs, and any unverified
claim. A source build is not a production artifact; the release package is
built by the command below.

## Release packaging

One command publishes `GameSaves.App` and builds the Windows installer:

```powershell
powershell -ExecutionPolicy Bypass -File build/publish-release.ps1
```

It needs the .NET 10 SDK and, for the installer, Inno Setup 6.3 or newer
(`winget install JRSoftware.InnoSetup`). `signtool` from the Windows SDK is
needed only when signing. The script:

1. reads the version from `<Version>` in
   `Manager/GameSaves.App/GameSaves.App.csproj`, the single source for the
   executable's file properties, the installer, and Settings > Diagnostics;
   bump it before a release. The Win32 `app.manifest` identity stays
   `1.0.0.0`, because Windows uses it only for side-by-side activation and
   never shows it;
2. publishes `GameSaves.App` self-contained for `win-x64` in Release as a
   single folder (no single-file bundle, no trimming, no ReadyToRun), through
   the isolated artifacts path, into `artifacts/release/publish/win-x64/`,
   and fails if that folder contains anything from the CLI, Reviewer, tests,
   or capture harnesses;
3. signs `GameSaves.App.exe` only when a certificate is supplied
   (`-CertificatePath` and `-CertificatePassword`, or the
   `GSM_SIGNING_CERTIFICATE` and `GSM_SIGNING_PASSWORD` variables); otherwise
   it prints that the package is unsigned. No certificate is in the repository;
4. compiles `build/GameSaveManager.iss` into
   `artifacts/release/GameSaveManager-Setup-<version>.exe`: a per-user install
   under `%LOCALAPPDATA%\Programs\Game Save Manager` without elevation, with
   `LICENSE` and `THIRD-PARTY-NOTICES.md` beside the program, upgraded in
   place; the installer is signed under the same condition. Without Inno
   Setup the script stops after the publish with exit code 2 and says so;
5. writes `GameSaveManager-Setup-<version>.exe.sha256` beside the installer,
   after signing: the lowercase SHA-256 hex, two spaces and the file name, as
   `sha256sum` writes it. The in-app update check (RELEASE-002) downloads this
   file from the release and refuses an installer whose hash differs, so the
   installer and its digest must be published together and unchanged.

Nothing under `bin/` or `obj/` is read or written, the application is never
started, and `%LOCALAPPDATA%\GameSave\` is never touched. The output folder is
git-ignored. `ReleasePackagingTests` pins what the scripts may publish and
install; no test runs them.

A package counts as released only after the pre-release checks above, this
command run end to end, and an install test on a machine without a .NET
runtime, each recorded as run or as unverified. The release workflow below
runs all three on a clean GitHub runner for every push and every tag.

### Cutting a release

1. Bump `<Version>` in `Manager/GameSaves.App/GameSaves.App.csproj` and run
   the pre-release checks.
2. Regenerate the migration fixture set for the new version (see "Migration
   fixtures" below) and check it in; `MigrationFixtureTests` fails until the
   database fixture for the bumped version exists.
3. Push the commit to `main` and wait for the **Release** workflow to pass:
   it builds, tests, packages with `build/publish-release.ps1` and runs the
   recovery gates on a clean runner (see below). The installer, its digest
   and the script output can be downloaded from the run as the
   `GameSaveManager-Setup-<version>` artifact.
4. Tag that commit `v<version>` and push the tag: exactly `v` plus the
   project version, because the in-app update check parses the tag and
   refuses any other shape, and the workflow fails the tag before building
   when it does not equal `<Version>`. The workflow then repeats the build,
   the tests, the packaging and the gates on the tag and creates the GitHub
   release with `GameSaveManager-Setup-<version>.exe` and
   `GameSaveManager-Setup-<version>.exe.sha256` attached under exactly those
   names; the app refuses a release that lacks either. The release page is
   what **Release notes** opens. A draft or pre-release is never offered:
   the app reads `releases/latest`, which GitHub resolves to the newest
   published full release, and the workflow never creates one.
5. From an older installed version, press Check now in Settings > Diagnostics
   and record whether the release was offered, downloaded, verified and
   installed. That is the only proof of the update path; until it is done the
   path is recorded as unverified.

### The release workflow and its recovery gates

`.github/workflows/release.yml` runs on every push to `main`, every pull
request to `main`, every `v*` tag, and by hand (**Run workflow**). It needs
no secret: without one the package is unsigned and the run says so. Three
jobs:

1. **Build, test and package** (`windows-latest`): reads `<Version>` and, on
   a tag, fails unless the tag is `v<Version>` (the error names both values
   and says no release was created); `dotnet build` in Release with
   `--no-incremental` and `-warnaserror`, so one warning fails the run;
   `dotnet test` for the whole solution; Inno Setup from the runner image
   (Chocolatey installs it if an image drops it);
   `powershell -ExecutionPolicy Bypass -File build/publish-release.ps1`, the
   same command as above, with its console output saved; and the upload of
   `GameSaveManager-Setup-<version>.exe`, `.exe.sha256` and
   `publish-release.log` as one artifact named
   `GameSaveManager-Setup-<version>`. When the repository secrets
   `GSM_SIGNING_CERTIFICATE` (the PFX as base64) and `GSM_SIGNING_PASSWORD`
   exist, the PFX is written to the runner's temporary folder for the length
   of the packaging step, handed to the script through the variables it
   already reads, and deleted even when the script fails; GitHub masks both
   values in the log. Without the secret the step prints a notice that the
   package is unsigned, and the script prints its own warning.
2. **Recovery gates** (a second `windows-latest` runner with no
   `setup-dotnet` step): downloads the artifact and runs
   `build/recovery-gates.ps1`. Gate 1 installs silently
   (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) and asserts that the program
   is under `%LOCALAPPDATA%\Programs\Game Save Manager` with the packaged
   version and that `%LOCALAPPDATA%\GameSave` does not exist. Gate 2 proves
   the start without a .NET SDK or runtime: `hostfxr.dll`, `hostpolicy.dll`
   and `coreclr.dll` sit beside the program, its `runtimeconfig.json` has
   `includedFrameworks`, and `GameSaves.App.exe --run-job <random id>`
   returns the refused outcome (2) with `PATH` cut down to `System32` and
   `DOTNET_ROOT` pointing at an empty folder, where a framework-dependent
   program stops with "hostfxr.dll could not be found". That exit code means
   the program composed every service, created and migrated its database,
   and declined an unknown job. Gate 3 runs the same installer again (an
   upgrade in place), asserts that the data folder's listing (every file
   with its size and SHA-256) is unchanged, and starts the upgraded program
   the same way. Gate 4 writes a future row into `schema_migrations` (the
   fabrication `MigrationFixtureTests` uses, through the runner's Python),
   starts the program again and expects the failed outcome (4) with the
   listing unchanged: the RELEASE-003 refusal. Gate 5 uninstalls silently
   and asserts that the install folder is gone and that the data folder's
   listing still equals gate 4's. The data-folder assertion is therefore
   "install, upgrade and uninstall change nothing there; only the program's
   own starts do", because the headless start itself creates the database.
   Install, upgrade and uninstall logs are uploaded as `recovery-gate-logs`.
3. **Publish the GitHub release** (tag only, after both jobs passed):
   `gh release create` with the two assets, `--verify-tag`, `--latest` and
   generated notes, never `--draft` or `--prerelease`.

Reading a failed gate: the step log shows `gate N pass: ...` for every gate
that passed and `gate N FAIL: ...` with the expected and actual values for
the one that did not (two listings are printed in full when they differ),
and `recovery-gate-logs` holds Inno Setup's own logs. A failure in gate 2 or
3 means the package does not start on a clean machine; in gate 4 that an
older program wrote to or accepted a newer database (a RELEASE-003
regression); in gate 5 that the uninstaller left files behind or touched
user data. The script refuses to run unless `GITHUB_ACTIONS` is `true` and
neither folder exists, because it writes a future migration row into the
data folder it finds; to run it on a throwaway machine, set
`GITHUB_ACTIONS=true` and `RUNNER_TEMP` in that session. It never deletes
anything under `%LOCALAPPDATA%\GameSave`. `ReleasePackagingTests` pins the
workflow and the gate script: the script call, the asset names, the tag
check, the secret handling, the gate job without an SDK, the real exit codes
and the no-delete rule.

### Migration fixtures and the newer-schema refusal

Every released version leaves a fixture set under
`Manager/GameSaves.Tests/Fixtures/`: `database/v<version>.sql`, a plain-SQL
dump (schema objects, then one `INSERT` per row) of the database as that
version's migrator, curated-mapping seeder and repositories write it after
representative use, with a header naming the app version and the schema
version; and `settings/ui-settings-v<schema>.json` and
`settings/sync-settings-v<schema>.json` as the two stores save them. Older
settings versions (UI v2 to v10, sync v0 and v1) are small hand-written
documents shaped after the schema notes in the stores. The database fixture
carries a placeholder row in `protected_sync_secrets` that the secret store
reports as unreadable; no fixture holds a readable secret or real user data.

`MigrationFixtureTests` replays every database fixture into a temporary file,
runs the real migrator, and asserts that the schema equals a freshly created
database's and that every row reads back unchanged in every column the fixture
had; loads every settings fixture to the current record with the documented
defaults; and fails when no database fixture exists for the current
`<Version>`, or when a fixture claims a schema the migrator does not know. To
regenerate the current version's set (never from real data; everything is built
in a temporary folder):

```powershell
dotnet build Manager/Manager.sln -c Release --artifacts-path Manager/.artifacts -v q --nologo
Manager/.artifacts/bin/GameSaves.Tests/release/GameSaves.Tests.exe -explicit only -method "GameSaves.Tests.MigrationFixtureTests.RegenerateFixturesForTheCurrentVersion"
```

Fixtures of released versions are immutable: a migration added during
development leaves them as they are and is proven by the replay; the next
release adds the next set.

A database that records a migration newer than the running build knows
(`schema_migrations` holds an id above `SchemaMigrator.LatestKnownVersion`) is
refused before anything is written: the app opens a small window saying that
the database was written by a newer version of Game Save Manager and that
nothing was changed, the headless `--run-job`/`--run-sync` host returns the
failed outcome code, and `migrate-dry-run` prints the same sentence and exits
1. `MigrationFixtureTests` proves the first two against a database with a
fabricated future migration row and checks that the file's bytes are identical
afterwards.
