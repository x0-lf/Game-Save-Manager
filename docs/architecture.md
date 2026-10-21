# Architecture

This guide owns project boundaries and dependency direction. Runtime behavior
is documented in the audience-specific guides linked from the
[documentation hub](README.md).

## Projects

| Project | Responsibility | Project references |
| --- | --- | --- |
| `GameSaves.Core` | Domain models, enums, contracts, and provider-neutral policy, including the manifest comparison that reports how two backup runs differ | None |
| `GameSaves.Infrastructure` | Filesystem, SQLite, registry, Steam, transfers, secret storage, SFTP, Google Drive, and Windows Task Scheduler integrations | Core |
| `GameSaves.App` | Main Avalonia UI and composition root; `--run-job <id>` runs one scheduled backup and `--run-sync <id>` one scheduled sync through the same composition without a window | Core, Infrastructure |
| `GameSaves` | Developer CLI, catalog management, verification, backup checks, and PCGamingWiki harvesting | Core, Infrastructure |
| `GameSaves.Reviewer` | Independent mapping curation application and its SQLite access | None |
| `GameSaves.Tests` | Shared regression coverage across Core, Infrastructure, CLI, and App behavior | App, Core, CLI, Infrastructure |
| `GameSaves.UiCapture` | Development-only headless visual capture harness | App |
| `GameSaves.UiMaterialCapture` | Development-only interactive Windows material capture harness | App |

Core has no project or package dependencies. Infrastructure references Core and
owns runtime integrations. App consumes both and keeps provider SDK objects out
of its models and commands. The CLI owns the harvesting workflow. Reviewer stays
self-contained so normal application behavior cannot depend on the curation tool.

Avalonia is limited to App, Reviewer, and the two capture harnesses. Google API
packages and Google SDK types are limited to Infrastructure. Neither capture
harness is shipped and no other project references them.

The release package is built outside the solution by `build/publish-release.ps1`
and `build/GameSaveManager.iss` at the repository root. It publishes
`GameSaves.App` alone, self-contained, and installs it per user; the CLI,
Reviewer, tests, and capture harnesses never ship. The version comes from the
App project file only. See [development](development.md) for the command and
the [safety model](safety-model.md) for what the installer may never touch.

The in-app update check (RELEASE-002) is the one place the App reads about
itself from the internet. `GameSaves.Core.Updates` holds the feed and
downloader contracts, `GameSaves.Infrastructure.Updates` the GitHub Releases
feed and the digest-verifying downloader, and `ApplicationUpdateViewModel` in
the App ties them to Settings and the Dashboard. The headless scheduled-run
host never resolves any of them.

## Dependency direction

```text
App -------> Core
App -------> Infrastructure ------> Core
CLI -------> Core
CLI -------> Infrastructure ------> Core
Tests -----> App, Core, CLI, Infrastructure
UiCapture -> App
UiMaterialCapture -> App
Reviewer    (independent)
```

External types stop at Infrastructure boundaries. Core contracts carry
project-owned models, stable provider kinds, safe results, and cancellation.
App and CLI compose those contracts rather than reaching around them to SQLite,
DPAPI, Google Drive, SSH, the registry, Windows Task Scheduler, or VDF parsing.

## Data ownership

- SQLite persistence and schema initialization live in Infrastructure, except
  the deliberately independent Reviewer repository.
- Remote profile settings contain allowlisted non-secret values. Authentication
  uses the secret-store contract.
- Backup manifests are the content identity used by restore and synchronization.
- Provider capabilities describe catalog potential; the current provider guide
  separately documents which controls the UI actually exposes.

## Change rules

- Put policy in the shared layer all callers already use.
- Keep presentation projects thin and provider-neutral.
- Do not introduce another application, provider abstraction, token store, or
  persistence route when an existing owner already covers the behavior.
- Preserve the [safety invariants](safety-model.md) at every boundary.
