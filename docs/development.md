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
claim. Release packaging is roadmap work; a source build is not a production artifact.
