# Windows Material Regression Baseline

> **Status:** The source-level cause is confirmed. Interactive Windows captures
> and effective transparency readings are still required before UI-001 through
> UI-006 can be marked complete.

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

## Windows capture record

The managed environment cannot write Avalonia BuildServices telemetry and
cannot perform an interactive desktop capture. These fields therefore remain
pending; no screenshot or platform result is inferred from headless output.

| Revision | Theme | Desktop | Requested | Effective | Main window | Detached window |
| --- | --- | --- | --- | --- | --- | --- |
| `e2b744e` | Dark / Light / System | White / black / representative | Pending | Pending | Pending | Pending |
| Current working tree | Dark / Light / System | White / black / representative | Pending | Pending | Pending | Pending |

Run the Windows procedure in the development guide under identical display,
Windows, theme, accent, size, and background conditions. Store sanitized PNGs
and the completed TSV under `artifacts/ui-material-windows/`; that directory is
ignored and is outside production assets.
