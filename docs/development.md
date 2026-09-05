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

Every capture mode, gallery modes included, runs against a throwaway working
directory: a new database, a new interface-settings file, a new sync-settings
file, a Steam locator and fallback scanner that find nothing, and a Google
Drive service that is never asked to authenticate. No real save, backup,
credential, account, or filesystem path can reach a capture.

## Gallery capture

Gallery mode produces the screenshots the website uses, and doubles as visual
evidence for review. It differs from the regression modes in one way only: it
runs against a populated, deterministic **showcase fixture** instead of the
empty state, because a marketing page showing "no games found" says nothing
about the product. The fixture lives entirely in the harness
(`GameSaves.UiCapture/Gallery/GalleryShowcase.cs`); no production view model
carries a marketing constant.

```powershell
# Deterministic renders: the archive, the text-scale sweep, and the headless
# half of the website set.
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-gallery gallery

# Real Windows composition: Acrylic, Mica, floating panels, detached windows.
dotnet run --project Manager/GameSaves.UiMaterialCapture/GameSaves.UiMaterialCapture.csproj -- artifacts/ui-gallery gallery

# Check what was produced: every image described, present, and truthful.
dotnet run --project Manager/GameSaves.UiCapture/GameSaves.UiCapture.csproj -- artifacts/ui-gallery gallery-verify
```

Narrower modes exist for iterating: `gallery-full` (archive only),
`gallery-curated` (website set only), `gallery-accessibility` (text scales
only) and `gallery-layout` (workspace and navigation only). The interactive
harness takes `gallery` (a subsampled material archive plus the website set),
`gallery-full` (every material cell the plan defines) and `gallery-curated`
(website set only).

### Two harnesses, one plan

`GameSaves.UiCapture/Gallery/GalleryPlan.cs` is the single list of what exists.
Each scenario names the engine that can render it truthfully, and each harness
captures only its own scenarios:

| Engine | Renders | Cannot render |
| --- | --- | --- |
| `avalonia-headless` | Everything with no window material; byte-stable per commit | Acrylic, Mica, anything with more than one window |
| `windows-screen-readback` | The composited desktop, so real materials and real multi-window layouts | Nothing deterministically: the OS decides |

The interactive harness needs an unlocked session that nobody is using. A
locked screen refuses a read-back outright, and a read-back returns whatever is
on the screen, so any other window in front of the capture area would be saved
as if it were the application. The harness therefore samples the capture region
on a grid before every read-back and refuses unless every point belongs to one
of its own windows; it waits for the area to clear and stops with a plain
message if it does not. Do not use the machine while it runs.

Acrylic and Mica are drawn by the Windows compositor, not by the application.
Avalonia's own render never contains the backdrop, so a headless capture that
requested a material still reports `effectiveMaterial: none` — it is not
evidence of that material and is never selected for the website. The
interactive harness records the transparency level Windows actually granted
next to the one requested; when the platform substitutes or denies a level the
capture is kept, marked as a fallback, and dropped from the selection.

High Contrast forces opaque surfaces by design, so a High Contrast capture also
reports `effectiveMaterial: none`. There is deliberately no "High Contrast with
Mica" image; it would be a picture of something the application never does.

### Output

Two sets, kept apart on purpose:

| Path | What it is |
| --- | --- |
| `artifacts/ui-gallery/full/` | The QA archive: the page/theme/accent/material/resolution matrix, the High Contrast pass, and the 85/100/125/150% text-scale sweep. Hundreds of images. Never published. |
| `artifacts/ui-gallery/selected/` | The website set: roughly fifty images, each with a caption and alt text. |
| `artifacts/ui-gallery/gallery-manifest.json` | Every image, with the full scenario, the engine, the effective material, notes, the source commit, a SHA-256 and a perceptual hash. |
| `artifacts/ui-gallery/gallery-selected.json` | The website subset in presentation order. |
| `artifacts/ui-gallery/accessibility-layout-report.md` | Per page and text scale: PASS, MINOR, or FAIL, measured on the arranged visual tree. |

Each harness writes a `manifest-<engine>.json` fragment and then rebuilds the
two combined files from every fragment present, so running one harness gives a
valid partial manifest and running both gives the complete one.

Resolutions: the website set is captured at exactly `1280x720` and `1336x768`.
The existing `layout` mode keeps its `1366x768` acceptance coverage; gallery
mode does not touch it.

### Adding a page, provider, or scenario

1. Add rows to the showcase fixture in `GalleryShowcase` if the page needs data.
2. Add scenarios to `GalleryPlan` — `Full()` for the archive, `Curated()` for
   the website, with a caption and alt text.
3. If the scenario needs a window material or more than one window, set
   `Engine = GalleryEngines.WindowsScreenReadback`.
4. Extend the coverage rules in `GalleryVerification` so the new dimension is
   required rather than optional.
5. `dotnet test --filter FullyQualifiedName~Gallery` checks the plan without a
   display; the capture run checks the images.

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
are throwaway, so no real user data can reach a capture, and it refuses to read
back any region another application is covering. Leave the machine alone while
it runs.

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
