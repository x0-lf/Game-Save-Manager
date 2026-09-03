# Roadmap

This file owns active and future work. Historical milestones cannot change the
status here. Every item states its product outcome, dependency, and evidence
required for completion.

## Now

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| DOC-020 | Independent reader journeys | A new reader can use the documentation without relying on author knowledge | DOC-001 through DOC-019 | An independent reader completes the prospective-user, contributor, and maintainer scripts with no unanswered question |
| UI-004 | Windows material regression baseline | None, Acrylic, and Mica have reproducible Windows evidence | Interactive Windows display with sanitized data | Preferred-reference and current captures record requested/effective levels across themes and bright/dark backgrounds |
| UI-001 | Restore Acrylic and Mica | Each supported material produces its distinct live Windows result without losing later features | UI-004 | None remains safely opaque by default; exact Acrylic and Mica requests work in main and detached windows; unsupported composition falls back safely |
| UI-002 | Protect navigation over materials | Navigation and transient menus remain readable without disabling the content backdrop | UI-001 | Primary and Settings navigation plus popup surfaces are opaque and readable across themes, accents, positions, collapse states, and High Contrast |
| UI-005 | Opaque primary rail surface | The complete primary rail remains readable over bright and dark backdrops | UI-001 and UI-004 | Rail chrome and tab strip pass expanded/collapsed left/right/top Windows captures without changing navigation behavior |
| UI-006 | Opaque Settings category surface | Settings categories remain readable while its content can retain material | UI-001 and UI-004 | All seven categories pass live theme, accent, keyboard, focus, scrolling, and detached-layout checks |
| UI-003 | Material visual-regression matrix | Maintainers detect material, fallback, and navigation-opacity regressions | UI-001 and UI-002 | Automated semantic sweep passes and the interactive Windows matrix records approved main/detached results for every required dimension |

DOC-020 review scripts:

- **Prospective user:** identify support status, requirements, first safe backup,
  deletion boundaries, available providers, and Google Drive limitations.
- **Contributor:** build and test all projects, locate architecture owners,
  follow provider Definition of Done, and report an unverified platform check.
- **Maintainer:** locate data paths, mapping trust rules, release checks, active
  backlog, security response, dependency record, and historical acceptance.

## Next

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| MAINT-001 | SFTP test seam | SFTP upload and download behavior can be tested without a live SSH server | Existing remote-filesystem boundary | Provider accepts an injectable boundary and deterministic upload/download tests pass without changing behavior |
| MAINT-002 | xUnit migration | The suite no longer depends on deprecated xUnit 2.9.3 | Stable suite baseline | Supported xUnit packages run the full suite with documented baseline changes |
| MAINT-003 | Retry-After support | Google Drive waits according to safe server guidance while preserving retry bounds | HTTP response observation and retry delay carrier | All Drive client paths propagate bounded `Retry-After`; deterministic tests cover valid, missing, and excessive values |
| SYNC-001 | WebDAV provider | Users can sync backup runs with a compatible WebDAV or Nextcloud server | MAINT-001 and provider DoD | Authentication, preview, upload, download, conflict, cancellation, safety, docs, and deterministic tests pass |
| SYNC-002 | OneDrive provider | Users can sync backup runs with OneDrive | Provider DoD and release OAuth decision | OAuth, root ownership, shared engine behavior, live acceptance, and documentation pass |

## Later

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| DOC-021 | Sanitized screenshots | Stable user guides include useful, non-personal UI images | UI content and release material stabilization | Approved current screenshots contain no account data, paths, saves, credentials, tokens, email, or remote IDs |
| SYNC-003 | Multi-target sync | One backup set can be synchronized to multiple explicitly selected profiles | Stable provider behavior | Preview and history identify each target; failures remain isolated; no target is implicit |
| SYNC-004 | Quota and health UI | Users can inspect provider health or quota where the provider safely supports it | Provider-specific APIs and privacy review | UI reports only verified capability data and degrades cleanly when unavailable |
| BACKUP-001 | Compressed backups | Users can opt into a documented compressed backup format | Format and migration design | Restore, integrity, import/export, compatibility, and failure recovery are proven |
| BACKUP-002 | 7z support | Users can import or export an explicitly supported 7z format | BACKUP-001 and dependency/license review | Format safety, traversal guards, limits, licenses, and round-trip tests pass |
| AUTO-001 | Scheduled backup and sync | Users can schedule safe preview-derived operations | Release host and unattended-safety model | Missed runs, locked saves, credentials, confirmation policy, cancellation, and history are defined and tested |
| DIFF-001 | Backup diff viewer | Users can compare two backup runs without changing either | Stable manifest model | Added, removed, and changed files are reported deterministically with no extraction side effects |
| REL-001 | Release packaging | Users receive a reproducible supported package with update and migration checks | DATA-001 and platform support decisions | Clean build, package inventory, licenses, install/update/rollback, and artifact verification pass |

## Research

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| SEC-001 | Client-side encryption | Users can optionally encrypt backup content before remote upload | Threat model and key-recovery design | A reviewed design covers keys, recovery, manifests, streaming, migration, and failure without data-loss claims |
| PLAT-001 | Linux discovery | Linux Steam libraries and saves can be discovered safely | Cross-platform data paths and secret store | Supported distributions, paths, permissions, packaging, and regression fixtures are defined and proven |
| PLAT-002 | Proton and Wine | Windows-game saves inside prefixes can be mapped without unsafe path guessing | PLAT-001 and prefix model | Prefix ownership, user selection, mapping expansion, and containment are documented and tested |
| PLAT-003 | Steam Deck | Handheld desktop and game-mode workflows are supported | PLAT-001 and PLAT-002 | Install, discovery, UI, storage, permissions, and restore workflows pass on hardware |
| PLAT-004 | macOS discovery | macOS Steam libraries and saves can be discovered safely | Cross-platform data paths and secret store | Supported versions, paths, permissions, packaging, and regression fixtures are defined and proven |
| SEC-002 | Cross-platform secret stores | Tokens can be protected on supported Linux and macOS systems | PLAT-001 or PLAT-004 | Secret Service and Keychain ownership, migration, deletion, corruption, and platform tests pass |

Research items move to Later or Next only after their dependencies and safety
model are concrete. Research is not an implementation commitment.

## Blocked

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| DOC-011 | Database migration and recovery guide | Users and maintainers have safe, versioned database migration, rollback, corruption, and recovery instructions | DATA-001, currently undefined | DATA-001 defines supported schemas, backup/restore ownership, failure modes, and tested recovery commands |
| DOC-013 | Validate documented Avalonia build commands | The solution and every project-specific build command are confirmed in the target user environment | Normal user shell with writable Avalonia BuildServices data | All seven Release builds and the solution build pass; warnings and failures are recorded |
| DOC-016 | Publish verified Wiki or article links | Repository docs can point to maintained external explanations without ambiguity | Authoritative published URLs | Maintainer verifies ownership, URL, scope, and non-authoritative status before any link is added |
| DOC-017 | Validate App, Reviewer, and UiCapture workflows | Maintainers have current runtime and visual evidence for desktop commands | Windows display plus normal user shell; provider checks may also need credentials/network | App and Reviewer launch, UiCapture default/layout/rail modes run, and sanitized results are reviewed |

## Completed

| ID | Title | Product outcome | Dependency | Completion criteria |
| --- | --- | --- | --- | --- |
| DOC-001 | Replace root README | Readers get a concise overview, present status, quick start, safety summary, and primary links | None | Root README contains no detailed CLI, archived chronology, or developer OAuth procedure |
| DOC-002 | Create documentation hub | Every guide and policy has a discoverable owner | DOC-001 | Ownership table, audience labels, and linking rules cover the documentation tree |
| DOC-003 | Create getting-started guide | Users can identify runtime/source requirements and begin safely | DOC-002 | .NET 10, Windows support, source run, data path, and first-run sequence are documented |
| DOC-004 | Create desktop guide | All nine current tabs and desktop workflows have one owner | DOC-002 | Startup loading, page refresh, layouts, detached tabs, recovery, workflows, and UI states are documented with “Transfer profiles” |
| DOC-005 | Create safety guide | User-data invariants and every deletion category are explicit | DOC-002 | Preview, overwrite, containment, sync, cleanup, profile/preset/credential deletion, and temp cleanup are distinguished |
| DOC-006 | Create architecture guide | Project and SDK boundaries reflect actual references | DOC-002 | All seven projects, dependency direction, Avalonia ownership, and Google SDK ownership match project files |
| DOC-007 | Create development guide | Contributors have root-relative commands and environment requirements | DOC-002 | Solution/project builds, tests, CLI, Reviewer, App, UiCapture, cleanup, troubleshooting, and release gates are covered |
| DOC-008 | Create database and mappings guide | Database location and mapping trust lifecycle have one owner | DOC-002 | Current path, review states, CLI overview, and project-local guide links are correct without claiming DATA-001 work |
| DOC-009 | Replace provider guide | Current provider behavior and UI limitations are separated from history | DOC-005 and DOC-006 | Five-provider matrix, safety, performance, testing gaps, Google limits, and provider DoD are documented |
| DOC-010 | Rewrite Google developer setup | Developers can configure minimal private OAuth safely | DOC-009 | Cloud/API/consent/client tasks, exact scope, environment values, ignore rules, incident response, and smoke test are present |
| DOC-012 | Archive A-Z chronology | Completed Google Drive milestone sequence remains available without controlling current status | DOC-009 | A-Z outcomes and acceptance dates have a visible historical banner and link to evidence |
| DOC-014 | Correct policies and templates | Contribution, security, license, pull-request, and feature-request guidance matches current provider and roadmap state | DOC-002 | Stale statements are removed; restrictions and provider documentation DoD remain explicit |
| DOC-015 | Validate repository links | Every repository-relative Markdown link resolves with exact tracked-file casing | Documentation rewrite | Dependency-free link check passes over all Markdown files |
| DOC-018 | Archive Google Drive acceptance | Detailed closed evidence remains available without controlling current status | DOC-009 | Existing acceptance record is preserved, labelled historical, and supplemented with unique setup-guide results |
| DOC-019 | Review Markdown rendering | Tables, code fences, banners, and navigation render correctly on GitHub | DOC-001 through DOC-018 | GitHub-compatible structural preview finds no broken table or fence |
| PRODUCT-001 | Saved remote profiles | Users can create, update, Save As, rename, select, and explicitly delete non-secret Local Folder, SFTP, and Google Drive profiles | Provider profile persistence | Implemented behavior and secret cleanup are covered; it is not listed as future work |
