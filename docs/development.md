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
```

Use xUnit filters for feedback, then run the complete suite before submission:

```powershell
dotnet test Manager/GameSaves.Tests/GameSaves.Tests.csproj --filter "FullyQualifiedName~GoogleDrive"
dotnet test Manager/GameSaves.Tests/GameSaves.Tests.csproj --filter "FullyQualifiedName~Sync"
```

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
```

The default mode captures tabs, themes, widths, accents, and representative
workspace states. `layout`, `rail`, and `material` run focused acceptance
sweeps. Material mode verifies app-owned opacity and navigation semantics and
writes `material-report.tsv`. It simulates accepted composition because the
headless renderer cannot reproduce or report the Windows Acrylic/Mica backdrop;
its `actual` column therefore says `headless-unavailable` rather than claiming
platform support.

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

The automated material sweep covers every value across focused combinations.
On an interactive Windows test account containing only synthetic or sanitized
data, repeat the matrix for the combinations that depend on the OS compositor:

1. Use the same Windows build, display scaling, app size, test data, and desktop
   backgrounds for `e2b744e` and the current working tree. Use `git archive` or
   another separate directory; do not reset or revert the working tree.
2. For each row, record the requested `TransparencyLevelHint` and the window's
   `ActualTransparencyLevel`. Capture the main window and one detached tab.
3. Confirm None is opaque by default; Acrylic visibly blurs/translucently shows
   the background; and Mica has a visibly distinct Mica backdrop.
4. Confirm the primary rail, Settings category strip, context menus, submenus,
   tooltips, and layout-recovery menus remain opaque and readable.
5. Change theme, accent, material, rail position, and collapse state while the
   relevant window is open. No restart is expected when Windows accepts a live
   change.
6. Turn on High Contrast and confirm both requested and effective material are
   None and every surface is opaque.
7. Record a denied request as an unsupported-platform fallback. Treat an
   effective non-None level different from the request as a failed matrix row,
   not as successful Mica or Acrylic.

Store approved screenshots and the observed-value report under
`artifacts/ui-material-windows/`, never in production assets. The current
source comparison and pending Windows evidence are recorded in the
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
claim. Release packaging is roadmap work; a source build is not a production artifact.
