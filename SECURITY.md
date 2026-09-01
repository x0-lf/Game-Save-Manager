# Security Policy

## TL;DR

- Report vulnerabilities privately. Do not place exploit details, credentials, save data, account information, or private infrastructure details in a public issue.
- Game Save Manager protects provider authentication, validates paths, previews destructive-looking operations, and defaults to copy-only/no-overwrite behavior. These boundaries are security-sensitive and must not be bypassed.
- Backup files, manifests, ZIP exports, and local or mounted-folder sync content are not application-encrypted. Protect their storage with appropriate operating-system, disk, and remote-access controls.

## Purpose

Game Save Manager handles save files, backup archives, local filesystem paths, Steam account identifiers, remote-storage configuration, SFTP connections, and Google OAuth tokens. A defect can therefore affect confidentiality, integrity, availability, or a user's ability to recover valuable data.

This policy explains which versions are supported, how to report a vulnerability safely, what information maintainers need, what protections the project provides, and where the trust boundaries remain.

## Supported versions

Game Save Manager is currently developed as a pre-release project. The repository does not maintain multiple supported release branches or backport security fixes to older snapshots.

| Version | Security support |
| --- | --- |
| Current `main` branch | Supported |
| Latest published release, when one exists | Supported until replaced or explicitly marked unsupported |
| Older commits, archived builds, and abandoned branches | Not supported; reproduce against current `main` where practical |
| Third-party forks or modified builds | Not supported by this repository |

Security fixes normally land on `main`. A maintainer may ask a reporter to confirm that an issue still exists on the latest commit before triage continues. This does not require a reporter to retest a destructive proof of concept against real data.

The currently supported end-user environment is Windows with .NET 10. Registry-based Steam discovery and the DPAPI secret-store implementation are Windows-specific. Linux Secret Service and macOS Keychain implementations are planned but are not currently supported security boundaries.

## Reporting a vulnerability

### Preferred private channel

Use GitHub's private vulnerability reporting form:

<https://github.com/x0-lf/Game-Save-Manager/security/advisories/new>

Submit the report only if GitHub displays a private report form. Do not open a public issue containing vulnerability details.

### If private reporting is unavailable

Use a private contact method published on the [`@x0-lf` GitHub profile](https://github.com/x0-lf). If no private method is available, open a minimal public issue asking the maintainer to establish a private security-reporting channel.

That public issue must not include:

- the vulnerability or exploit details;
- reproduction steps;
- screenshots, logs, database rows, or affected files;
- account names, email addresses, local usernames, or private paths;
- hostnames, IP addresses, SFTP fingerprints, or Drive object IDs; or
- credentials, OAuth values, authorization URLs, tokens, keys, or secrets.

A suitable public title is `Security contact requested`. Continue only after a private channel has been established.

### What to include

Provide the smallest amount of information needed to reproduce and assess the problem:

- the affected commit, build, or release;
- the affected component and provider;
- operating system, architecture, and .NET version;
- the security impact and the boundary that was crossed;
- required attacker access or user interaction;
- deterministic reproduction steps using synthetic data;
- a minimal proof of concept, when one can be provided safely;
- whether live saves, backups, credentials, profiles, history, or remote objects changed;
- sanitized stable error codes or diagnostics;
- whether the issue is reliably reproducible on current `main`;
- a proposed remediation, if known; and
- any disclosure deadline or prior disclosure that maintainers should know about.

Use fictional values such as `user@example.invalid`, temporary directories, disposable profiles, and test-only remote accounts. Replace object IDs, account identifiers, hostnames, fingerprints, and paths with stable placeholders.

Never send an account password, private key, access token, refresh token, authorization code, client secret, downloaded OAuth credential file, complete application database, real save file, or raw provider response unless a maintainer has explicitly arranged an appropriate secure transfer and confirmed that the item is necessary. In most cases, these values are not necessary.

## Response process

This is a maintainer-run open-source project rather than a staffed security-response service. The following are response targets, not service-level guarantees:

| Stage | Target |
| --- | --- |
| Acknowledge a private report | Within 3 business days |
| Initial reproducibility and severity assessment | Within 7 business days |
| Status updates while remediation is active | At least every 14 days |
| Coordinated disclosure | Normally within 90 days, adjusted for severity and release readiness |

Maintainers will aim to:

1. acknowledge the report and establish a private communication channel;
2. confirm the affected boundary without requesting unnecessary sensitive data;
3. classify severity, scope, exploitability, and user-data impact;
4. identify supported versions and any required containment advice;
5. develop regression tests and a narrowly scoped fix;
6. verify that the fix preserves transfer, backup, restore, secret-store, and provider safety invariants;
7. publish a security advisory or release note when warranted; and
8. credit the reporter if they request attribution.

If no acknowledgement arrives within five business days, send one private follow-up. If no private channel is available, a minimal public security-contact request is acceptable, but vulnerability details must remain private.

## Coordinated disclosure

Please allow maintainers a reasonable opportunity to investigate and distribute a fix before publishing technical details. The reporter and maintainers should agree on a disclosure date appropriate to the severity, evidence of exploitation, affected users, and remediation complexity.

Maintainers may accelerate disclosure when users need immediate defensive guidance or when exploitation is already public. They may request additional time when a safe fix requires architectural changes, but they will not ask a reporter to keep an unresolved vulnerability secret indefinitely.

Public advisories should avoid unnecessary personal data, live credentials, reusable tokens, private paths, and complete exploit payloads. CVE assignment will be considered when the vulnerability and release model warrant it; it is not guaranteed for every accepted report.

## Security scope

### Examples of in-scope vulnerabilities

Reports are especially valuable when they demonstrate one or more of the following:

- arbitrary file read, write, overwrite, extraction, or deletion outside an explicitly selected and validated root;
- path traversal, ZIP path escape, symlink or reparse-point escape, or containment-check bypass;
- bypass of preview, explicit confirmation, no-overwrite defaults, backup-before-overwrite, manifest validation, or cleanup containment;
- deletion or modification of live saves, backup runs, remote runs, archives, or sync history outside the documented operation;
- replacement of immutable backup-run content through a mutable-metadata path;
- use of an unavailable provider, silent provider fallback, or execution of an unconfirmed sync plan;
- cross-profile or cross-provider secret access, deletion, or token collision;
- plaintext persistence or disclosure of OAuth tokens, passwords, passphrases, private-key material, or protected secret bytes;
- unsafe Google OAuth behavior, including broader-than-documented scopes, missing PKCE/state protection, an embedded credential prompt, or insecure callback handling;
- SFTP host-key verification bypass, unsafe trust-state replacement, or unintended credential persistence;
- SQL injection or unsafe deserialization that crosses a repository, profile, or filesystem boundary;
- a malicious archive, manifest, settings file, mapping, remote response, or database row causing code execution or unauthorized filesystem access;
- untrusted harvested mappings becoming executable without the required approval boundary;
- sensitive data entering logs, exceptions, diagnostics, screenshots, profile JSON, ordinary SQLite fields, or operation history;
- an exploitable vulnerable dependency or compromised build/release artifact; or
- denial of service that is remotely triggerable or causes persistent corruption or loss of user data.

Credible data-loss defects should be reported privately when public reproduction details would materially increase risk. A non-sensitive correctness bug with no security impact may use the normal [bug report form](https://github.com/x0-lf/Game-Save-Manager/issues/new?template=bug_report.yml).

### Usually out of scope

The following are normally handled as bugs, feature requests, or upstream reports unless they cross a documented security boundary:

- missing roadmap functionality, including WebDAV, OneDrive, and other work
  explicitly listed as unavailable in the current roadmap;
- inaccurate or incomplete unapproved harvested save-path candidates;
- the wording of a third-party consent screen when the application still requests only its documented scope;
- vulnerabilities in Google, Steam, PCGamingWiki, an SFTP server, the operating system, or another third-party service that are not caused or meaningfully amplified by this project;
- attacks requiring an already fully compromised operating-system account with no additional boundary crossed;
- unsupported operating systems or third-party forks;
- reports based only on automated scanner output without a project-specific impact analysis;
- dependency version reports that do not show reachability or impact in Game Save Manager; and
- social engineering, denial-of-service testing against public services, or testing accounts and systems you do not own or have permission to assess.

When uncertain, report privately. Maintainers would rather reclassify a good-faith report than have sensitive details disclosed publicly.

## Project security model

### Protected authentication data

Provider authentication is owned by the existing `ISecretStore` boundary.

- On Windows, `WindowsDpapiSecretStore` protects secret payloads with DPAPI using `DataProtectionScope.CurrentUser`.
- SQLite stores versioned protected BLOBs, not plaintext OAuth token fields.
- Secret identity uses the immutable remote-profile ID and a stable secret name.
- Google OAuth token payloads are allowlisted and versioned; the Google SDK file token store is not used.
- Google OAuth uses the system browser, a loopback callback, PKCE, and the exact `drive.file` scope.
- SFTP passwords and private-key passphrases are session-only and are cleared when relevant profile state changes.
- Secret cleanup is profile-specific and must never clear another profile or provider's data.

DPAPI protection is tied to the current Windows user and protection environment. Copying the database to another user or machine is not expected to preserve authentication. DPAPI is not a substitute for securing the Windows account or using full-disk encryption.

OAuth client IDs are application configuration rather than user secrets. A desktop OAuth client secret cannot be kept confidential, but it must still never be committed or included in diagnostics. Access tokens, refresh tokens, authorization codes, account passwords, SFTP passwords, private keys, and passphrases are secrets.

### User data and confidentiality

Game Save Manager does not currently provide application-level encryption for save data or backup content.

- Backup files and their manifests are stored as ordinary files.
- ZIP exports are not encrypted archives.
- Local-folder and mounted-folder synchronization relies on the destination filesystem's access controls.
- SFTP encrypts data in transit when the negotiated SSH connection and verified host key are trustworthy; files at rest remain protected by the remote server's controls.
- SQLite contains non-secret but potentially private metadata such as paths, profile names, account display information, timestamps, and operation history.
- Google account display name and optional email are treated as non-secret profile metadata but should still be handled as personal information.

Users should protect backup destinations, ZIP exports, application data, private keys, and remote accounts with suitable permissions, disk encryption, access control, retention, and offline backups.

SHA-256 manifest hashes detect content changes relative to the stored manifest. They provide integrity checking, not encryption, identity, or a cryptographic signature. An attacker who can modify both a backup file and its manifest may be outside the protection that a bare hash provides.

Game Save Manager copies save files as data; it does not scan them for malware. A malicious file can remain malicious after a faithful backup, restore, archive, or sync operation.

### Filesystem and data-safety invariants

Security-sensitive changes must preserve these established rules:

- Copy and backup operations do not move source data.
- Preview precedes execution, and execution requires explicit confirmation.
- Existing targets are skipped unless overwrite is explicitly enabled for a supported operation.
- Safe overwrite first creates and verifies an overwrite backup; failure blocks replacement.
- Paths are normalized and checked for containment during planning and again during execution where appropriate.
- ZIP import must not extract outside its temporary and final roots.
- Backup-run content is immutable and create-only.
- Mutable provider metadata is a separate contract restricted to `.gamesave-sync/`.
- Sync operates on completed backup runs, not live save folders.
- Sync remains copy-only and does not delete or overwrite remote backup runs.
- Manifests are uploaded last so interrupted remote folders remain incomplete.
- Conflicts are reported rather than resolved destructively.
- Cleanup is the explicit deletion exception: it is previewed, confirmed, and limited to recognized manifest-bearing runs inside the application backup base.
- Unavailable providers never silently fall back to another provider.

### Provider boundaries

- Local Folder, SFTP, and Google Drive are the currently implemented sync providers.
- SFTP uses trust on first use for a new host key, records the accepted fingerprint, and rejects later changes until the stored trust entry is explicitly handled.
- Google Drive synchronizes completed backup runs through the shared preview and
  execution engine. Upload is create-only, download is no-overwrite, conflicts
  are not copied, and remote deletion is unavailable.
- Google Drive object IDs are authoritative; names are display and resolution values only.
- Google Drive supports My Drive app-accessible objects under `drive.file`;
  shared drives, full Drive browsing, arbitrary folder picking, quota display,
  and open-in-browser controls are not available.
- Provider SDK types remain inside Infrastructure. Core and App consume project-owned, provider-neutral contracts.

### Harvested mappings

Save-path data collected from external sources is untrusted input. Harvested candidates remain pending or disabled until reviewed. Only mappings with `Approved` status may participate in normal transfer behavior. A source being public or widely used does not make its paths safe.

## Safe research expectations

Security research must use systems, accounts, servers, files, and data that you own or are explicitly authorized to test.

Please:

- use disposable profiles, temporary directories, synthetic saves, and test-only remote accounts;
- make a separate backup before testing any potential overwrite, restore, cleanup, archive, or sync defect;
- stop after demonstrating the minimum impact needed to validate the issue;
- avoid persistence, privilege escalation, lateral movement, and unnecessary data access;
- do not exfiltrate or retain user data;
- do not degrade Google, Steam, PCGamingWiki, SFTP hosts, or other third-party services;
- do not perform high-volume scanning or denial-of-service testing;
- respect provider terms, rate limits, and account policies; and
- securely delete sensitive test artifacts when the report is resolved.

If you accidentally access data that is not yours, stop immediately, do not copy or redistribute it, and report the event privately.

## Safe-harbor intent and bounty status

The maintainers intend to treat good-faith research performed within this policy as authorized project security research and will not pursue action against a reporter solely for accidental, proportionate violations made while following this policy. Report an accidental overstep promptly and cooperate in limiting harm.

This statement cannot authorize testing against third-party services, infrastructure, accounts, or data, and it does not bind third parties or law-enforcement authorities. You remain responsible for complying with applicable law and third-party terms.

This project does not currently operate a paid bug-bounty program. Reports are welcomed regardless of whether monetary compensation is available.

## If credentials or private data are exposed

Treat committed or publicly shared credentials as compromised even if the content is deleted shortly afterward.

1. Stop using and redistributing the exposed value.
2. Revoke affected access and refresh tokens.
3. Rotate passwords, client secrets, keys, or other credentials as appropriate.
4. Remove the value from the working tree and published artifacts.
5. Assess commits, forks, logs, caches, screenshots, databases, archives, and CI output for copies.
6. Notify maintainers privately with sanitized scope and containment details.
7. Rewrite Git history only when appropriate, understanding that history rewriting does not revoke a credential.
8. Add a regression guard or ignore rule when it can prevent recurrence.

Do not paste the exposed value into an issue, pull request, chat message, or follow-up report to prove that it existed.

## Dependency and supply-chain security

Dependency reports should identify the package, resolved version, advisory, affected project, reachable code path, and practical impact. The repository checks both direct and transitive packages with:

```text
dotnet restore Manager/Manager.sln
dotnet list Manager/Manager.sln package
dotnet list Manager/Manager.sln package --include-transitive
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
dotnet list Manager/Manager.sln package --deprecated
```

An advisory match is important evidence but does not by itself establish exploitability. Conversely, a transitive dependency is not automatically harmless. Maintainers will evaluate reachability, runtime behavior, available framework mitigations, compatible upgrades, and regression risk. Use `dotnet nuget why <project> <package>` to identify the originating dependency before choosing a remediation.

Vulnerable dependencies must be upgraded or removed. Do not suppress an advisory, add an exclusion, or add unrelated framework-package references merely to make an audit appear green. For packages that carry native runtime assets, inspect clean publish output and its `.deps.json` in addition to the restored graph. Security package updates must preserve existing database, parser, transfer, backup, authentication, and provider behavior through regression tests.

Steam VDF parsing uses `ValveKeyValue` 0.20.0.417 only through Infrastructure.
The CLI, Core, and App do not directly own parser packages or expose
parser-specific types. This replaces the obsolete Gameloop dependency chain
rather than suppressing the vulnerable legacy framework packages it introduced.

Do not submit replacement binaries, opaque generated archives, downloaded credential files, or vendored dependencies as a security fix without explicit maintainer agreement. Package and licensing changes must follow [CONTRIBUTING.md](CONTRIBUTING.md) and update [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) when required.

## Security verification for changes

Security-sensitive fixes should include deterministic regression tests and preserve the repository's architecture. Unless the change is documentation-only, verify at least:

```text
dotnet restore Manager/Manager.sln
dotnet test Manager/Manager.sln
dotnet build Manager/Manager.sln --configuration Release
dotnet list Manager/Manager.sln package --vulnerable --include-transitive
git diff --check
git status
```

Record the test count, warnings, errors, dependency findings, manual verification, and anything that remains unverified. Do not use a personal account, real credential, real save, or production server in the default automated test suite.

## Related policies and documentation

- [Contributing Guidelines](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Safety model](docs/safety-model.md)
- [Current provider behavior and security boundaries](docs/sync-providers.md)
- [Google Drive developer setup and credential handling](docs/google-drive-developer-setup.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)
- [MIT License](LICENSE)

This policy complements the repository's license and contributor guidance. It does not create a warranty or guarantee that every vulnerability will be fixed on a particular schedule.
