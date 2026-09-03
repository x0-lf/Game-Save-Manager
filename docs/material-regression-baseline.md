# Windows Material Regression Baseline

> **Status:** Complete. The source-level cause is confirmed and the interactive
> Windows evidence has been captured for both the preferred reference commit
> and the current tree.

This report is the evidence baseline for None, Acrylic, and Mica. It does not
replace the current behavior described in the [desktop guide](desktop-app.md)
or the validation procedure in the [development guide](development.md).

## Source comparison

The material path is unchanged from the preferred reference in its important
ownership boundaries:

1. Settings persists `WindowMaterial` through `UiSettingsStore`.
2. `SettingsViewModel` applies theme resources before applying the material.
3. `WindowMaterialService` maps Acrylic to `AcrylicBlur` and Mica to `Mica`.
4. `App` attaches the main window, and `DetachedWindow` attaches every detached
   tab window to the same service.
5. `ThemeService` controls the app-owned page surface after the platform reports
   `ActualTransparencyLevel`.

| Revision | Requested levels | Confirmed material page alpha | Navigation surface | Observation |
| --- | --- | ---: | --- | --- |
| `e2b744e39a85db36c286a621fb8119161890396e` | None / AcrylicBlur / Mica | `0.00` | No independent opaque role | Preferred dark-material reference |
| `adc37de364a11f5351ebb9807e6055577cc6262d` | Unchanged | `0.92` | No independent opaque role | Introduced the page-opacity floor |
| `41d0904b2536d283965208adf017a54d69438d35` | Unchanged | `0.92` | No independent opaque role | Pre-Epic-B HEAD; Acrylic and Mica were largely obscured |
| Epic B working tree | Exact requested level required | `0.00` | Always-opaque semantic brush | Restores the backdrop while protecting navigation |

The regression mechanism is the `0.92` theme-surface floor introduced after
`e2b744e`, not a missing Acrylic/Mica mapping. The same page brush also sat
behind navigation, so simply restoring alpha `0.00` would make navigation
depend on the desktop. Epic B separates those concerns.

The service now accepts composition only when every shown attached or detached
window reports the exact requested level. `None`, a denied level, or a platform
substitution keeps the opaque fallback. High Contrast continues to request
None regardless of the stored material.

## How the Windows evidence is produced

`GameSaves.UiMaterialCapture` runs the real application on the real Win32
platform against a throwaway database, a throwaway settings file, and a Steam
locator that finds nothing, so no real user data reaches a capture. It drives
the whole matrix in one process without a restart, and every number is read
back from the composited screen, because Windows — not the app — draws the
backdrop:

- `requested` is the window's `TransparencyLevelHint`; `actual` is the
  platform's `ActualTransparencyLevel`.
- `railBleed` and `stripBleed` are the mean per-channel difference of the
  navigation rail and the Settings category strip between an identical capture
  over a white and a black full-screen window. An opaque surface cannot change
  at all, so anything above noise is navigation transparency.
- `contentBleed` is the same measurement over the page content region.
- `contentVsNone` compares the same layout against material `none`. This is the
  decisive Mica measurement: Mica samples the desktop wallpaper rather than the
  window underneath, so the white/black test alone cannot see it.

See the [development guide](development.md) for the command and its exit codes.

## Windows capture record

Machine: Windows 11 Enterprise 10.0.26200, single display at 100% scaling,
Windows accent colour applied to title bars. Both revisions were captured with
the identical harness, window sizes, page (Settings), and backdrops. The
reference revision was built in a separate worktree; the repository was never
reset or reverted.

Captures and reports:

- Current tree: `artifacts/ui-material-windows/` (61 rows,
  `windows-material-report.tsv`, `run-head.log`).
- Preferred reference `e2b744e`: `artifacts/ui-material-windows-e2b744e/`
  (61 rows, `run-reference.log`).

Both directories are ignored and sit outside production assets.

### Platform support

Windows granted every requested level on this machine. No row fell back:

| Material | Requested | Effective | Main window | Detached window |
| --- | --- | --- | --- | --- |
| None | `None` | `Transparent` (Win32 baseline for a window that requests nothing) | Opaque, bleed `0.00` | Opaque, bleed `0.02` |
| Acrylic | `AcrylicBlur` | `AcrylicBlur` | Granted and visible | Granted and visible |
| Mica | `Mica` | `Mica` | Granted and visible | Granted and visible |
| Any, with High Contrast | `None` | `Transparent` | Opaque | n/a |

`ActualTransparencyLevel` reports `Transparent` for a window that requests
nothing; that is the Win32 baseline, not an applied effect, and the measured
bleed for those rows is zero. `WindowMaterialService` never treats it as an
active material because it only accepts a level equal to a non-`None` request.

### Result comparison

Mean per-channel difference between the white-backdrop and black-backdrop
capture of the same configuration (0 = fully opaque, 255 = fully transparent):

| Configuration | Rail bleed at `e2b744e` | Rail bleed at Epic B | Page bleed at Epic B |
| --- | ---: | ---: | ---: |
| Acrylic, Dark, left rail | `241.767` | `0.005` | `25.080` |
| Acrylic, Light, left rail | `243.211` | `0.006` | `25.080` |
| Acrylic, System, left rail | `243.211` | `0.006` | `25.080` |
| Acrylic, Dark, collapsed rail | `161.115` | `0.014` | `41.104` |
| Acrylic, Dark, each of the five accents | `241.767` | `0.005` | `25.080` |
| Mica, Dark | `0.000` (Mica ignores the window behind) | `0.005` | `0.000`, `contentVsNone` `2.856` |
| None, Dark | `0.000` | `0.005` | `0.000` |

The Settings category strip measured `0.000` in every Epic B row. It has no
named host at `e2b744e`, so that revision reports `n/a` for it.

The rail numbers are the regression, measured: at `e2b744e` the navigation rail
was as transparent as the page, so a bright window behind the application
showed straight through the navigation text. At the Epic B tree the same rail is
pixel-identical over white and black while the page still composites the
material.

Run outcomes:

| Revision | Rows | `material-visible` | `opaque-ok` | Failures | Exit code |
| --- | ---: | ---: | ---: | ---: | ---: |
| `e2b744e` | 61 | 22 | 21 | 18 (`fail-navigation-rail-leaks`, all Acrylic) | 1 |
| Epic B tree | 61 | 40 | 21 | 0 | 0 |

The reference revision has no named navigation-surface element, so its rail was
measured geometrically over the same left strip; the `railSource` column records
which method produced each row.

### Covered dimensions

Materials None/Acrylic/Mica; themes Dark/Light/System; accents Indigo, Teal,
Rose, Amber, Violet; rail left, right, top, expanded and collapsed; main and
detached windows; white, black, and the live desktop as backdrop; normal and
High Contrast. Live changes only — the process is never restarted, which is
also the evidence that a material change applies immediately.

### Known platform behavior, not application defects

- Mica does not sample windows behind the application, only the desktop
  wallpaper, so its white/black bleed is `0.00` by design. It is verified
  against the `none` baseline instead (`contentVsNone` `2.86` dark,
  `3.48` light, `4.67` detached).
- A window that requests no material reports `Transparent` as its effective
  level on Win32.
- The headless sweep cannot resolve the `system` theme (there is no OS theme to
  inherit), so it records `unresolved` for variant-scoped tokens on those rows.
  The interactive run covers the system theme.
