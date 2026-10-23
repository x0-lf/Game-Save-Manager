# Solution-Wide Definition of Done Contract

This document is the authoritative, solution-wide standard for declaring any task,
bugfix, backlog item, or contribution complete. Every completed card in
[docs/ROADMAP.md](ROADMAP.md) and every submission must satisfy this contract.

No item may be marked complete in the roadmap or committed until every applicable
pillar has verifiable evidence.

---

## The Six Pillars of Done

```text
+-------------------------------------------------------------------------+
|                  Solution-Wide Definition of Done                       |
+-------------------+-------------------+---------------------------------+
| 1. Code Quality   | 2. Automated Test | 3. Accessibility & UX           |
|  - 0 warnings     |  - Full suite pass|  - WCAG AA contrast             |
|  - Architecture   |  - Deterministic  |  - No color-only meaning        |
|  - Null safety    |  - Boundary tests |  - High Contrast mode           |
+-------------------+-------------------+---------------------------------+
| 4. Data Safety    | 5. Truth in Claims| 6. Docs & Traceability          |
|  - 11 invariants  |  - No fake claims |  - Authoritative guide updated  |
|  - Secret storage |  - Disclose skips |  - Implementation report        |
|  - Trust boundary |  - Platform facts |  - Roadmap card reconciled      |
+-------------------+-------------------+---------------------------------+
```

---

### Pillar 1: Code Quality & Compiler Cleanliness

1. **Zero Warnings and Zero Errors:**
   The entire solution must build with zero compiler warnings and zero errors in
   Release configuration without incremental builds:
   ```powershell
   dotnet build Manager/Manager.sln --configuration Release --no-incremental
   ```
2. **Strict Nullability:**
   Nullable reference types are enabled across all projects (`<Nullable>enable</Nullable>`).
   Nullability must be handled intentionally with clear ownership. Blanket
   compiler suppressions (`#pragma warning disable`, `!`) are prohibited unless
   provably bounded and documented.
3. **Architectural Boundary Enforcement:**
   Dependencies must follow strictly unidirectional layering:
   - `Core`: Domain models and contracts; zero project or package dependencies.
   - `Infrastructure`: Runtime integrations (SQLite, Steam, Google Drive, SFTP, DPAPI).
     Provider SDK packages (`Google.Apis.Drive.v3`, `SSH.NET`) must remain strictly
     encapsulated inside Infrastructure.
   - `App` and `CLI`: Depend on `Core` and `Infrastructure`; no direct provider SDK references.
   - `Reviewer`: Independent curation tool; cannot become a runtime dependency of the App.
   - `UiCapture` & `UiMaterialCapture`: Development-only tools; never shipped.
4. **Lifecycle & Cancellation Hygiene:**
   All async methods must accept a `CancellationToken`, restore busy states in
   `finally` blocks, and prevent stale async callbacks from modifying newer state.

---

### Pillar 2: Automated Testing & Determinism

1. **Test Coverage for All Changes:**
   Every bugfix, feature, or refactor must include regression tests in `Manager/GameSaves.Tests`
   at the lowest useful abstraction layer.
2. **100% Deterministic and Offline:**
   Automated unit tests must never require internet access, live Google accounts,
   OAuth tokens, live SSH servers, or real Steam installations.
   Use in-memory stores, recording fakes, synthetic runs, and controllable clocks.
3. **Zero Test Failures or Skips:**
   The full test suite must pass cleanly:
   ```powershell
   dotnet test Manager/Manager.sln --configuration Release
   ```
   Zero tests may fail or be skipped without prior recorded maintainer dispensation.
4. **Boundary & Prohibition Tests:**
   Where an architectural rule or safety boundary exists (e.g., no plain tokens in
   JSON, no provider fallback, no unapproved mappings in transfer), write tests that
   explicitly assert that forbidden behavior remains absent.

---

### Pillar 3: Accessibility (a11y) & UX Integrity

1. **WCAG Contrast Compliance:**
   - Text surfaces must meet or exceed WCAG 2.x AA contrast ratios: at least **4.5:1**
     for normal reading text and **3.0:1** for large text or graphical controls.
   - When Acrylic or Mica materials are active, text readability must be guarded
     against white backdrops using the semantic backdrop contrast shield (e.g. `OBS-021`).
2. **Information Never Conveyed by Color Alone:**
   All plan items, status chips, transfer outcomes, and severity levels must provide
   explicit semantic text and distinct accessible labels. Verify via `sync-state-report.tsv`
   or headless UI capture runs.
3. **High Contrast Mode Support:**
   In High Contrast mode, window materials must be disabled, and all navigation,
   categories, and content surfaces must remain completely opaque with system contrast colors.
4. **Keyboard & Focus Navigation:**
   All primary views must support keyboard navigation (`Ctrl+1` through `Ctrl+9`
   tab addressing, sequential tab stops, Enter/Space activation for toggles and buttons).

---

### Pillar 4: Data Safety & Security Invariants

All changes must uphold the 11 user-data invariants defined in [docs/safety-model.md](safety-model.md):

1. **Preview Before Execution:** Destructive or mutating operations (transfer, restore,
   cleanup, sync) require an explicit dry-run preview followed by separate user confirmation.
2. **Copy, Never Move:** Sources are never removed during backup, restore, or sync.
3. **No Silent Overwrite:** Existing targets are skipped by default.
4. **Safety Backup Before Overwrite:** Any supported overwrite must safely backup
   the existing target first; a backup failure refuses the overwrite.
5. **Path Containment:** Path traversal, `Zip Slip`, rooted relative paths, and writes
   outside designated target roots are rejected.
6. **Backup Run Synchronization Only:** Providers sync completed backup runs, never
   live save directories.
7. **Create-Only Remote Runs:** Run payloads and manifests are immutable and create-only.
8. **Manifest Last:** A remote folder is not complete until its manifest is written.
9. **No Automatic Conflict Resolution:** Mismatched same-name runs remain untouched.
10. **Strict Mapping Approval Trust Boundary:** Only mappings with `review_status == 'Approved'`
    and `enabled == true` may participate in backup, transfer, or restore operations.
11. **Protected Secrets:** Credentials and tokens are session-only or stored in DPAPI
    secret storage. They are never written to plain JSON, SQLite config tables, or logs.

---

### Pillar 5: Platform Verification & Truth in Claims

1. **Truth in Status:**
   Never claim complete platform support or successful verification for platforms or
   environments that were not tested.
2. **Explicit Disclosure of Unverified Checks:**
   If a platform check, live cloud check, SSH negotiation, or quota limit could not
   be verified on real infrastructure, explicitly record it as unverified in the
   accompanying documentation and report rather than assuming success.
3. **Simulation Separation:**
   Headless sweeps must record `headless-unavailable` rather than claiming platform
   backdrop composition.

---

### Pillar 6: Documentation & Backlog Traceability

1. **Authoritative Documentation Synchronization:**
   Update the single authoritative guide owning the subject (per [docs/README.md](README.md))
   in the same change as the code.
2. **Roadmap Reconciliation:**
   Reconcile [docs/ROADMAP.md](ROADMAP.md) by moving the completed item to `## Completed`,
   verifying that its canonical ID, title, product outcome, and observable completion
   criteria are satisfied.
3. **Implementation Report:**
   Author a comprehensive report under `docs/raports/YYYYMMDD-HHMM-<task-name>.md`
   recording the executive summary, delivered changes, verification evidence, and the
   exact continuation prompt for the next task.
4. **Commit Log Recording:**
   Record conventional commit notes in `commit.txt`. Do not execute `git commit`
   unless explicitly commanded by the user.

---

## Provider Definition of Done Extension

New or modified synchronization providers must satisfy the additional provider criteria
codified in [docs/sync-providers.md](sync-providers.md#provider-definition-of-done).

---

## Verification Sequence (Checklist)

Before declaring any task or PR complete, execute this verification checklist:

```powershell
# 1. Restore dependencies
dotnet restore Manager/Manager.sln

# 2. Verify all tests pass cleanly
dotnet test Manager/Manager.sln --configuration Release

# 3. Verify zero compiler warnings and errors
dotnet build Manager/Manager.sln --configuration Release --no-incremental

# 4. Audit packages for vulnerabilities
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
dotnet list Manager/Manager.sln package --deprecated

# 5. Check git hygiene (whitespace, trailing spaces)
git diff --check
git status --short
```
