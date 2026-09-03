# Documentation

This directory separates current guidance from completed milestone history.
The document listed as owner is authoritative for its subject; other documents
should link to it instead of maintaining a second explanation.

## Start here

- [Project overview and quick start](../README.md)
- [Getting started](getting-started.md)
- [Desktop application](desktop-app.md)
- [Safety model](safety-model.md) **Security-sensitive**
- [Current roadmap](ROADMAP.md)

## Maintainer guides

- [Architecture](architecture.md)
- [Development and verification](development.md)
- [Windows material regression baseline](material-regression-baseline.md) **Developer-only**
- [Database and save-path mappings](database-and-mappings.md)
- [Sync providers](sync-providers.md)
- [Google Drive developer setup](google-drive-developer-setup.md) **Developer-only; security-sensitive**

Exact CLI syntax belongs to `GameSaves -- help`. The project-local harvesting
and review procedures remain beside the executables that own them:

- [Testing mappings](../Manager/GameSaves/Help/HowToTest.md) **Developer-only**
- [Harvesting mappings](../Manager/GameSaves/Help/HowToHarvest.md) **Developer-only**
- [Harvesting multiple batches](../Manager/GameSaves/Help/HowToHarvestMultiple.md) **Developer-only**
- [Reviewing mappings](../Manager/GameSaves.Reviewer/Help/HowToReviewMappings.md) **Developer-only**

## Policies and records

- [Contributing](../CONTRIBUTING.md)
- [Security policy](../SECURITY.md) **Security-sensitive**
- [Code of Conduct](../CODE_OF_CONDUCT.md)
- [Third-party notices](../THIRD-PARTY-NOTICES.md)
- [License](../LICENSE)
- [Google Drive milestone chronology](history/google-drive-roadmap.md) **Historical; not current status**
- [Google Drive acceptance evidence](history/google-drive-acceptance.md) **Historical; not current status**

## Ownership

| Subject | Authoritative owner |
| --- | --- |
| Project overview and quick start | [Root README](../README.md) |
| Documentation navigation and ownership | This file |
| Runtime and source-build requirements | [Getting started](getting-started.md) |
| Desktop workflows and UI states | [Desktop application](desktop-app.md) |
| User-data safety invariants | [Safety model](safety-model.md) |
| Project boundaries and dependencies | [Architecture](architecture.md) |
| Build, run, test, troubleshooting, release checks | [Development](development.md) |
| Windows material regression evidence | [Material regression baseline](material-regression-baseline.md) |
| Mapping lifecycle and CLI overview | [Database and mappings](database-and-mappings.md) |
| Provider behavior, capabilities, limits, and performance | [Sync providers](sync-providers.md) |
| Developer Google OAuth configuration | [Google Drive developer setup](google-drive-developer-setup.md) |
| Active and future work | [Roadmap](ROADMAP.md) |
| Closed Google Drive chronology and evidence | [History](history/google-drive-roadmap.md) |
| Contribution policy | [Contributing](../CONTRIBUTING.md) |
| Vulnerability reporting and security boundaries | [Security policy](../SECURITY.md) |
| Dependency licenses | [Third-party notices](../THIRD-PARTY-NOTICES.md) |
| Exact CLI commands | `GameSaves -- help` |

## Linking rules

- The root README links only to primary guides and policies; this hub indexes everything.
- Detailed documents link to the authoritative owner rather than repeating it.
- Developer-only, security-sensitive, and historical links are labelled.
- Project-local harvesting and Reviewer guides stay beside their executables.
- Links use repository-relative, case-exact paths and avoid heading fragments.
- Repository documentation is authoritative. A Wiki or external article cannot override it.
- No Wiki or article is linked until its authoritative URL is verified. None is
  currently verifiable from the public repository.
