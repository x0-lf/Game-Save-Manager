# Game Save Manager

Game Save Manager is a .NET 10 desktop application for discovering Steam games
and profiles, backing up save data, restoring verified backups, transferring
saves between local Steam profiles, and synchronizing completed backup runs.

The Avalonia desktop app is the user-facing product. The command-line project
remains developer tooling for discovery, verification, catalog work, and
PCGamingWiki harvesting.

## Current status

Local Folder, SFTP, and Google Drive synchronization are implemented. WebDAV
and OneDrive appear in the provider catalog but cannot be configured or used.
Google Drive completed controlled live acceptance on 2026-08-20; the remaining
provider limitations are documented in the current [provider guide](docs/sync-providers.md).

The application is pre-release and Windows is the primary supported environment.
There is no packaged end-user release workflow yet.

## Quick start from source

Install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet restore Manager/Manager.sln
dotnet run --project Manager/GameSaves.App/GameSaves.App.csproj
```

Start with disposable data or copies. Preview every operation before confirming
it, and keep an independent backup of saves you cannot replace.

## Safety summary

- Transfer, restore, cleanup, and synchronization are previewed before execution.
- Existing targets are skipped by default; supported overwrites are explicit and
  backed up first.
- Synchronization copies completed backup runs, not live save directories.
- Sync never overwrites or deletes local or remote backup runs.
- Backup cleanup is the only feature that deletes user backup content, and it is
  limited to confirmed manifest-bearing runs inside the application backup base.

Read the complete [safety model](docs/safety-model.md) before trusting the app
with important data.

## Repository layout

The solution contains seven projects:

```text
Manager/
|-- GameSaves.Core/           Domain contracts and models
|-- GameSaves.Infrastructure/ Runtime and provider integrations
|-- GameSaves.App/            Avalonia desktop application
|-- GameSaves/                Developer CLI and harvesting tools
|-- GameSaves.Reviewer/       Independent mapping-review application
|-- GameSaves.Tests/          Shared xUnit regression coverage
`-- GameSaves.UiCapture/      Development-only UI capture harness
```

## Documentation

- [Documentation hub](docs/README.md)
- [Getting started](docs/getting-started.md)
- [Desktop application](docs/desktop-app.md)
- [Safety model](docs/safety-model.md)
- [Sync providers](docs/sync-providers.md)
- [Roadmap](docs/ROADMAP.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

For contributor setup, run:

```powershell
dotnet test Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
dotnet run --project Manager/GameSaves/GameSaves.csproj -- help
```

Complete commands and environment-specific limitations are in the
[development guide](docs/development.md).

## Disclaimer

This software is provided without warranty under the [MIT License](LICENSE).
Save formats, Steam layouts, provider behavior, and third-party software and 
dependencies used by this project remain under their
own licenses. Review every preview and keep independent backups.

This project contains a combination of developer-written code and AI-assisted contributions.
Primarily tests and the UI screenshot harness, and selected implementation tasks.

In some cases, AI-generated code may not represent the most optimized or ideal solution and
has been integrated in its current form for development and evaluation purposes.
All AI-assisted changes are reviewed and verified before being committed.
Additional code audits, testing, optimization, and refactoring are planned before release. 

The current version should be considered an experimental, early-alpha release of Game Save Manager.
Features, behavior, and internal implementation details may change significantly before the planned public release later this year.
