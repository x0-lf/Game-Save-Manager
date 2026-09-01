# Contributing to Game Save Manager

Thank you for considering a contribution to Game Save Manager. Contributions are welcome in code, tests, documentation, save-path research, accessibility, design, and careful bug reporting.

This project can read and copy live save data, maintain backup history, store protected authentication, and communicate with remote providers. A contribution is therefore successful only when it is useful, maintainable, and demonstrably safe for user data.

## TL;DR

- Follow the [Code of Conduct](CODE_OF_CONDUCT.md) and keep collaboration respectful and constructive.
- Keep each contribution focused on one approved problem or roadmap scope.
- Preserve architectural boundaries and never weaken data-safety controls as an incidental change.
- Add deterministic tests, run the complete verification suite, and disclose anything not verified.
- Never submit credentials, personal data, private save content, generated artifacts, or unrelated changes.

## Quick contributor checklist

Before submitting a change:

- read the [Code of Conduct](CODE_OF_CONDUCT.md), the
  [documentation hub](docs/README.md), and the authoritative guide for the change;
- keep the change focused on one problem or roadmap milestone;
- preserve the repository's architectural and data-safety boundaries;
- add deterministic regression tests for changed behavior;
- run the full test suite and Release build;
- disclose anything that was not tested or could not be verified;
- update documentation when behavior, configuration, dependencies, or roadmap status changes; and
- remove credentials, account data, local paths, generated files, and unrelated changes from the final diff.

## Do not include in pull requests

Check the final diff and repository status carefully. Do not include:

- OAuth tokens, client secrets, passwords, private keys, downloaded credential files, or plaintext token caches;
- personal email addresses, account identifiers, Google Drive IDs, private hostnames, or user-specific filesystem paths;
- real save files, private database contents, unredacted logs, or screenshots containing personal information;
- `bin/`, `obj/`, IDE state, local databases, personal tool configuration, or other generated and machine-specific files;
- large raw harvested datasets without an explicit provenance, licensing, and repository-storage decision;
- unrelated formatting, dependency upgrades, documentation rewrites, or changes belonging to another task; or
- claims that tests, manual verification, provider support, or roadmap work are complete when they are not.

## Code of Conduct

Participation in this project is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Be respectful, keep reviews constructive, protect private information, and report conduct or sensitive security concerns through a private maintainer channel.

## Ways to contribute

Useful contributions include:

- reproducible bug reports;
- focused fixes with regression coverage;
- implementation of an explicitly scoped roadmap task;
- tests that strengthen data-loss, path-containment, authentication, or provider-boundary protection;
- accessibility and usability improvements that preserve safety gates;
- documentation corrections and developer guidance;
- dependency maintenance with an accompanying license and vulnerability review; and
- carefully sourced and reviewed save-path mappings.

For broad features, schema changes, new providers, new dependencies, or changes to safety policy, discuss the proposal with a maintainer before investing in a large implementation. This reduces duplicated effort and prevents a contribution from crossing a roadmap or architecture boundary that the project is not ready to support.

## Development prerequisites

The solution targets .NET 10. Install:

- the .NET 10 SDK or a compatible newer SDK capable of targeting .NET 10;
- Git;
- a development environment with C# and Avalonia support, if desired; and
- Windows for complete manual verification of Windows registry discovery and current-user DPAPI secret storage.

The provider-neutral Core and most automated tests are not inherently Windows-specific, but Windows is currently the primary environment for the complete application feature set.

Internet access is required to restore packages and for developer commands that intentionally access Steam, PCGamingWiki, SFTP, or Google services. The default automated regression suite must not require a browser, Google account, OAuth credentials, real SFTP server, or other personal external service.

## Supported environments

| Environment | Support level | Contributor guidance |
| --- | --- | --- |
| Windows 10 or 11 | Primary development and manual-verification environment | Required for complete verification of Steam registry discovery and Windows DPAPI secret storage. |
| Linux | Partial development environment | Provider-neutral code may build and run, but Windows-specific behavior and the complete desktop workflow are not currently claimed as fully supported. |
| macOS | Partial development environment | Provider-neutral code may build and run, but Windows-specific behavior and the complete desktop workflow are not currently claimed as fully supported. |
| .NET 10 SDK | Supported target toolchain | All projects target `net10.0`. Use this SDK for the most representative development environment. |
| Compatible newer .NET SDK | Supported for development | It must remain capable of targeting .NET 10. Do not change target frameworks as an incidental contribution. |
| Offline automated test environment | Supported after package restore | The regression suite must use deterministic fakes and temporary resources rather than personal external services. |
| Live Steam, SFTP, or Google services | Optional, feature-specific manual verification | Use only accounts, hosts, and test data you are authorized to use, and report results without personal values. |

When a change affects an environment you cannot test, state that limitation explicitly in the pull request.

## Getting started

Clone your fork and create a focused branch from the current `main` branch:

```bash
git clone https://github.com/<your-account>/Game-Save-Manager.git
cd Game-Save-Manager
git switch -c <short-purposeful-branch-name>
```

Restore, test, and build the complete solution:

```bash
dotnet restore Manager/Manager.sln
dotnet test Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
```

Run the desktop application:

```bash
dotnet run --project Manager/GameSaves.App
```

Inspect the developer CLI:

```bash
dotnet run --project Manager/GameSaves -- help
```

Run the internal mapping reviewer when working with harvested mapping candidates:

```bash
dotnet run --project Manager/GameSaves.Reviewer
```

Do not use real save data as disposable test input. Work with temporary directories, copies, or intentionally created fixtures.

## Repository structure

The main solution is `Manager/Manager.sln`.

| Project or path | Responsibility |
| --- | --- |
| `Manager/GameSaves.Core` | Provider-neutral domain models, contracts, status types, and business rules. |
| `Manager/GameSaves.Infrastructure` | Filesystem, SQLite, registry, Steam, secret-store, SFTP, Google SDK, and other external integrations. |
| `Manager/GameSaves.App` | Main Avalonia desktop application, view models, views, and application composition. |
| `Manager/GameSaves` | Developer CLI for discovery, verification, catalog maintenance, backup testing, and harvesting workflows. |
| `Manager/GameSaves.Reviewer` | Internal Avalonia tool for reviewing harvested save-path candidates before they are trusted. |
| `Manager/GameSaves.Tests` | xUnit regression suite covering Core, Infrastructure, CLI, App view models, boundaries, and safety behavior. |
| `Manager/GameSaves.UiCapture` | Development-only headless UI capture and layout/rail acceptance harness. |
| `docs` | Current audience-specific guides, roadmap, and labelled historical evidence. |
| `THIRD-PARTY-NOTICES.md` | Dependency inventory, purpose, version, and license information. |

Do not create another application, test utility, credential manager, or token store when an existing project or abstraction already owns that responsibility.

## Architecture rules

### Core remains provider-neutral

`GameSaves.Core` may define project-owned interfaces, immutable models, results, enums, error codes, and business rules. It must not depend on:

- Avalonia or presentation types;
- SQLite or database implementation types;
- DPAPI or operating-system implementation types;
- Google, SSH.NET, or other provider SDK types; or
- concrete Infrastructure services.

Provider SDK objects and raw external exceptions must not cross a Core public API.

### Infrastructure owns integrations

`GameSaves.Infrastructure` implements Core contracts and contains platform and provider details. This includes filesystem access, SQLite repositories, protected secret storage, remote filesystem implementations, Google SDK usage, SFTP, and dependency-registration extensions.

Keep external behavior behind narrow, testable boundaries. Browser-, network-, filesystem-, database-, and clock-dependent behavior should be replaceable by deterministic fakes where practical.

### Presentation projects stay thin

The App and CLI should consume project-owned contracts and models. They must not bypass repositories or secret abstractions, issue raw Google Drive requests, construct provider SDK credentials, or duplicate Infrastructure policy.

The App may act as the composition root through existing registration helpers, but SDK types must not leak into its models, commands, or public APIs.

`GameSaves.Reviewer` is intentionally self-contained. Do not couple normal application behavior to this developer-only review tool.

### Dependency direction

The intended direction is:

```text
Presentation and developer tools
    -> GameSaves.Core contracts and models
    -> GameSaves.Infrastructure implementations at composition boundaries

GameSaves.Infrastructure
    -> GameSaves.Core
    -> external SDKs and platform APIs
```

When adding a type, place it in the layer that owns the policy rather than the layer that happens to call it first.

## Mandatory data-safety rules

The following rules are part of the product design, not optional implementation details:

1. **Preview before execution.** Copy, restore, cleanup, and sync operations expose a dry-run or plan before changing data.
2. **Explicit confirmation.** Execution requires a separate user action and the relevant confirmation state.
3. **Copy by default.** Transfer and synchronization do not move source data.
4. **No silent overwrite.** Overwrite is disabled by default. Where overwrite is explicitly supported, the existing backup-before-overwrite policy must remain intact.
5. **Backup-run synchronization only.** Remote synchronization operates on completed backup runs, never directly on live save folders.
6. **Immutable backup content.** Backup-run files and manifests are create-only. Explicitly mutable provider metadata uses separate operations and remains restricted to `.gamesave-sync/`.
7. **No remote deletion or conflict guessing.** Sync never silently deletes or overwrites remote runs and never resolves content conflicts arbitrarily.
8. **Manifest last.** A remote backup run is complete only after its manifest has been uploaded successfully; interrupted folders without a manifest remain incomplete.
9. **Path containment.** Resolve and validate paths before use and again at execution where required. Reject traversal, rooted paths, and writes outside the intended base.
10. **No provider fallback.** An unavailable or unimplemented provider must not silently create or use a different provider.
11. **Scoped cleanup only.** Cleanup applies only to explicitly selected, manifest-bearing application backup runs inside the authorized backup base. It never deletes live saves or arbitrary custom destinations.
12. **Auditable outcomes.** Return and display specific copied, skipped, blocked, warning, and failed results without leaking sensitive information.

Any proposal to change these rules requires explicit maintainer discussion, dedicated regression coverage, and corresponding documentation. Do not weaken a safeguard as an incidental part of another feature.

## Roadmap and scope discipline

The [current roadmap](docs/ROADMAP.md) defines work to perform and work that
must remain out of scope. Historical milestone files cannot change its status.
When implementing an item or slice:

- inspect the current implementation and latest relevant commits first;
- preserve completed milestone behavior;
- implement only the requested surface;
- do not activate later providers or features prematurely;
- do not mark a checklist item complete until its code, automated verification, required manual verification, and documentation are complete;
- state clearly when live or platform-specific verification remains pending; and
- leave later milestones unchecked.

Avoid using display names or UI text as behavioral identifiers. Prefer stable kinds, capabilities, enums, IDs, and typed settings.

## Coding practices

Follow the style of the surrounding project. There is currently no repository-wide formatter configuration, so keep formatting changes local and avoid mechanical churn unrelated to the contribution.

General expectations:

- keep nullable reference types enabled and handle nullability deliberately;
- use clear ownership and single-purpose types rather than ambiguous Boolean modes;
- use `Async` suffixes for asynchronous methods and accept `CancellationToken` where operations can block;
- restore busy state in `finally` blocks or equivalent safe lifecycle handling;
- prevent stale asynchronous results from updating a different profile, provider, or operation generation;
- prefer immutable records or models for values and results where that matches existing code;
- assign explicit numeric enum values when values are persisted, cross a stable contract, or are protected by regression tests;
- use stable, non-secret error codes and friendly messages instead of passing raw external exception text to the UI;
- use ordinal comparison when IDs, exact provider values, or exact remote path segments require case preservation;
- dispose streams, provider clients, `DriveService`, and other short-lived resources deterministically;
- keep service registration free of I/O, browser launches, token reads, and network calls; and
- document reasoning where a provider or platform cannot offer the same atomicity guarantees as a local filesystem.

Avoid speculative abstractions. Introduce a shared helper only when it centralizes real policy or removes contradictory implementations.

## Error handling and diagnostics

Expected failures should map to typed, provider-neutral results where the caller needs to make a policy or UI decision. Distinguish cancellation, not found, ambiguity, invalid configuration, authentication failure, access denial, rate limiting, temporary unavailability, persistence failure, and unexpected failure when those distinctions affect safe behavior.

Diagnostics may include sanitized operation names, stable error codes, retryability, HTTP status, and allowlisted provider reasons. They must not include:

- tokens, authorization codes, credentials, or secrets;
- raw OAuth or provider responses;
- authorization or request URLs with query values;
- personal email addresses or account data;
- local paths containing personal information; or
- Google Drive object IDs in routine messages or `ToString()` output.

Expected cancellation is not a generic failure and must not leave a view model or service permanently busy.

## Tests

Every behavior change should include regression tests at the lowest useful boundary. The current suite uses xUnit in `Manager/GameSaves.Tests`.

Tests should be:

- deterministic and independent of execution order;
- offline by default;
- isolated through temporary directories and temporary databases;
- based on in-memory stores, recording fakes, controllable clocks, and fake provider boundaries where appropriate;
- free of personal accounts, credentials, installed-game assumptions, and machine-specific paths; and
- explicit about safety behavior and prohibited side effects.

Use fictional data such as:

```text
Example User
user@example.invalid
```

Do not require a real browser, Google account, Google Cloud project, OAuth client, SFTP server, Steam installation, or real Drive folder in the automated suite.

Run all tests from the repository root:

```bash
dotnet test Manager/Manager.sln
```

During development, a focused filter can shorten feedback time:

```bash
dotnet test Manager/GameSaves.Tests/GameSaves.Tests.csproj --filter FullyQualifiedName~GoogleDrive
```

A focused run does not replace the full solution test before submission.

When changing a boundary, include tests that prove forbidden behavior remains absent. Examples include no SDK types in Core or App, no plaintext token store, no provider fallback, no overwrite, no out-of-scope scope, and no late asynchronous result updating the wrong profile.

## Manual verification

Some behavior requires careful manual verification, particularly Avalonia UI flows, Windows DPAPI, installed Steam discovery, real SFTP compatibility, and Google desktop OAuth.

Manual testing must:

- use test data or a private development account controlled by the contributor;
- start with backups or disposable fixtures;
- confirm both intended results and absence of destructive side effects;
- avoid screenshots or reports containing personal information;
- record results generically, without account emails, tokens, folder IDs, hostnames, or private paths; and
- clearly state when verification was not performed.

Never report a manual scenario as passed merely because an automated fake passed.

## Google Drive contributions

Read [docs/sync-providers.md](docs/sync-providers.md) and [docs/google-drive-developer-setup.md](docs/google-drive-developer-setup.md) before changing Google Drive code.

Mandatory boundaries include:

- request exactly `https://www.googleapis.com/auth/drive.file` unless a separately reviewed future requirement proves another scope necessary;
- do not add full Drive, readonly, metadata, `appDataFolder`, OpenID, email, or profile scopes incidentally;
- keep Google SDK packages and types inside Infrastructure;
- use the system browser, loopback callback, PKCE, and the supported installed-app flow for interactive authorization;
- persist tokens only through `ISecretStore`; never use `FileDataStore` or a plaintext token cache;
- treat Client ID and optional desktop client secret as developer-local configuration, not profile or database data;
- use My Drive only until shared-drive support is explicitly implemented;
- treat Drive file and folder IDs as authoritative and names as display or exact-resolution values;
- escape Drive queries centrally, follow every list page, exclude trashed items, and reject duplicate ambiguity;
- never expose raw provider exceptions, account metadata, object IDs, or OAuth values in safe results; and
- keep uploads create-only, downloads no-overwrite, manifests last, conflicts
  unresolved, and remote deletion unavailable;
- do not claim quota display, arbitrary Drive folder picking, open-in-browser,
  shared drives, or full Drive browsing unless each behavior is implemented and verified; and
- preserve the current provider factory and shared-engine route instead of
  reimplementing sync policy in Google-specific code.

Google tests must use fake authorizers, fake API boundaries, in-memory secret stores, and temporary repositories. Local OAuth environment variables are never required for the default suite.

Never commit:

- `GAMESAVES_GOOGLE_CLIENT_ID` or `GAMESAVES_GOOGLE_CLIENT_SECRET` values;
- downloaded OAuth credential JSON;
- access or refresh tokens;
- test-user email addresses;
- account screenshots;
- Drive folder or file IDs from a personal account; or
- Google SDK token-cache files.

If local Google configuration is needed, follow the developer guide and configure it outside the repository.

## Save-path mappings and harvested data

Harvested or scraped data is candidate data, not trusted application data. A source may be incomplete, outdated, platform-specific, incorrectly parsed, or subject to licensing requirements.

When contributing mappings:

- retain source and license provenance;
- follow rate limits and use a truthful, contactable user agent when accessing external services;
- run small controlled harvests rather than parallel or repository-wide requests;
- leave new harvested candidates disabled or pending;
- review candidates with `GameSaves.Reviewer`;
- approve only paths that have been checked for the intended platform and profile behavior;
- run verification and a backup dry-run after approval; and
- do not commit large raw harvest datasets without an explicit repository and licensing decision.

Detailed workflows are documented in:

- `Manager/GameSaves/Help/HowToHarvest.md`;
- `Manager/GameSaves/Help/HowToHarvestMultiple.md`;
- `Manager/GameSaves/Help/HowToTest.md`; and
- `Manager/GameSaves.Reviewer/Help/HowToReviewMappings.md`.

Only mappings with `Approved` review status may be trusted by normal transfer behavior.

## Persistence and secrets

When changing SQLite or serialized settings:

- preserve ownership of existing fields;
- use existing repositories and serializers rather than writing SQL from the App;
- keep runtime state out of persisted profile data;
- use explicit allowlisted DTOs for provider settings;
- preserve unknown-user-data safety and migration behavior;
- add migration and raw-row regression tests where a schema changes; and
- verify that profile JSON and ordinary SQLite rows contain no secret values.

Authentication belongs in the protected secret store under a stable owner and secret name. Display names, provider names, and account emails are not valid secret owner IDs.

Secret cleanup must be narrowly scoped. A profile-specific operation must never clear another profile's token, an SFTP secret, or all application secrets.

## Dependencies and licensing

Avoid adding a package when the platform or an existing dependency already provides the required behavior. If a package is necessary:

1. explain why it is needed and which project owns it;
2. use the narrowest appropriate project reference;
3. inspect direct and transitive packages;
4. review known vulnerabilities;
5. verify that its license is compatible with the project; and
6. update `THIRD-PARTY-NOTICES.md` with the package, version, license, and purpose.

Run:

```bash
dotnet list Manager/Manager.sln package
dotnet list Manager/Manager.sln package --include-transitive
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
```

Do not silently upgrade unrelated dependencies in a feature contribution. Note pre-existing advisories separately from advisories introduced by the change.

## Documentation

Update documentation in the same contribution when changing:

- user-visible behavior or limitations;
- environment variables or developer setup;
- provider architecture or scope;
- security or token-storage behavior;
- database or serialization ownership;
- dependencies or third-party licensing; or
- roadmap completion state.

Keep claims precise. Differentiate implemented behavior, configuration-only behavior, planned behavior, automated verification, and live verification. Do not describe future functionality as available.

Use generic example values and sanitized test results. Never place personal configuration in documentation.

Follow the ownership and linking rules in [docs/README.md](docs/README.md).
Update the authoritative owner instead of copying its content elsewhere. Current
guides must not take status from `docs/history`, and Wiki or article links must
not be added until an authoritative URL is verified.

For every new or changed sync provider, update
[docs/sync-providers.md](docs/sync-providers.md) as part of the provider
Definition of Done. Update the developer setup guide, security policy, roadmap,
and third-party notices when their owned facts change. Do not add a manually
maintained “last reviewed” date without a release process that keeps it accurate.

## Commit guidance

Keep commits focused, reviewable, and buildable where practical. Write a short imperative subject that explains the outcome. Recent repository history commonly uses prefixes such as:

```text
feat: Add safe Google Drive ID caching
fix: Preserve authentication after cancelled reconnect
test: Cover remote metadata replacement
docs: Document provider architecture
```

Use a body when the reason, safety impact, migration, or important limitation is not obvious from the diff. Do not include generated outputs, local databases, IDE state, credentials, or unrelated formatting.

Do not rewrite or discard another contributor's work merely to make a branch cleaner. Resolve overlapping changes deliberately.

## Pull requests

GitHub automatically loads the repository's [pull-request template](.github/pull_request_template.md) when a new pull request is opened. Complete every applicable section; use `Not applicable` with a short explanation instead of deleting safety or verification prompts.

Open a pull request with a clear title and a description that includes:

- the problem and intended outcome;
- the implementation approach;
- affected architecture boundaries;
- user-data, authentication, and provider-safety implications;
- automated tests added or updated;
- exact verification commands and results;
- manual verification performed, including anything still pending;
- documentation, dependency, schema, or configuration changes; and
- explicit confirmation of important out-of-scope behavior that remains unavailable.

For UI changes, include sanitized screenshots when they materially help review. Do not include account information, local paths, saved-game contents, remote IDs, tokens, or credentials.

Keep the pull request focused. Separate unrelated fixes rather than hiding them in a large feature diff. Respond to review comments with either a change or a concrete technical explanation.

### Required verification

Unless the contribution is documentation-only, run:

```bash
dotnet restore Manager/Manager.sln
dotnet test Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
git diff --check
git status
```

Record the test count, build warnings, and build errors. Compare warnings with the baseline and do not introduce new warnings without explaining and justifying them.

Documentation-only changes do not normally require a complete build, but they still require manual review, working links, `git diff --check`, and a clean, intentional status.

### Pull-request checklist

- [ ] The change has one clear purpose and respects the current roadmap scope.
- [ ] Core, Infrastructure, App, CLI, and Reviewer ownership remains correct.
- [ ] Data-safety invariants are preserved.
- [ ] No unrelated provider or feature was activated.
- [ ] Regression tests cover the changed behavior and important failure paths.
- [ ] The full test suite passes, or an exact blocker is disclosed.
- [ ] The Release solution builds, and new warnings are explained.
- [ ] Cancellation, concurrency, and stale-result behavior were considered where relevant.
- [ ] Logs, exceptions, results, fixtures, and screenshots contain no secrets or personal data.
- [ ] Dependencies and `THIRD-PARTY-NOTICES.md` are correct.
- [ ] User and developer documentation is accurate.
- [ ] The final diff contains no generated files, local databases, credentials, or unrelated edits.

## Reporting bugs and proposing features

Use the structured forms so maintainers receive the information needed for a safe review:

- [Open a bug report](https://github.com/x0-lf/Game-Save-Manager/issues/new?template=bug_report.yml)
- [Propose a feature](https://github.com/x0-lf/Game-Save-Manager/issues/new?template=feature_request.yml)

The template sources are available at [bug_report.yml](.github/ISSUE_TEMPLATE/bug_report.yml) and [feature_request.yml](.github/ISSUE_TEMPLATE/feature_request.yml).

A useful bug report includes:

- the affected version or commit;
- operating system and relevant runtime details;
- the feature and provider involved;
- minimal reproduction steps;
- expected and actual behavior;
- sanitized status or error codes;
- whether user data was changed; and
- whether the issue is repeatable with test data.

Before posting, remove account names, emails, tokens, authorization URLs, Drive IDs, hostnames, local usernames, private paths, and save contents.

Feature proposals should explain the user problem, the smallest safe behavior that solves it, expected architecture ownership, and interaction with the roadmap and safety model. A proposal does not authorize implementation of later milestones.

## Security and private reports

Do not publicly disclose credentials, exploitable data-loss behavior, or sensitive provider responses. Use a private contact method published by a repository maintainer. If none is available, follow the minimal public-contact fallback described in [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) without including sensitive details in the public request.

## License

By contributing, you agree that your contribution may be distributed under the repository's [MIT License](LICENSE). Only submit work you have the right to contribute. Preserve required attribution and source-license information for imported or adapted material.

Thank you for helping make Game Save Manager safer, clearer, and more reliable.
