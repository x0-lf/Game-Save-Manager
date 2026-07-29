# Game Save Manager Code of Conduct

## TL;DR

- Treat contributors and users with respect, and keep technical disagreement constructive and focused on the work.
- Protect user data by preserving the project's architectural boundaries, safety controls, and honest verification requirements.
- Never expose credentials or personal information; report conduct, security, privacy, and credible data-loss concerns through a private maintainer channel.

## Purpose

This Code of Conduct protects contributors and users by defining respectful behavior and safe engineering practices for a project that handles sensitive data, credentials, save files, and remote storage.

## Our commitment

Game Save Manager welcomes contributors and users of every background and experience level. We are committed to providing a respectful, inclusive, and safe environment in all project spaces.

This project handles save data, credentials, remote accounts, and filesystem operations. Good community conduct therefore includes both respectful communication and responsible engineering. Contributors are expected to protect users, preserve data, and describe the behavior and verification of their work honestly.

## Scope

This Code of Conduct applies to:

- repository issues, pull requests, reviews, discussions, and documentation;
- project-related chat, email, testing, and support conversations;
- public spaces where someone is acting as, or reasonably appears to be acting as, a representative of Game Save Manager; and
- private project communications when they affect participation in the community.

## Expected behavior

Community members are expected to:

- communicate respectfully and focus criticism on ideas, behavior, and code rather than people;
- welcome questions and different experience levels without ridicule or gatekeeping;
- give specific, actionable review feedback and explain important safety or architectural concerns;
- assume good faith while remaining willing to identify and correct concrete risks;
- acknowledge mistakes, accept reasonable correction, and help repair unintended harm;
- respect personal boundaries, privacy, and requests to stop unwanted contact;
- disclose relevant limitations, incomplete verification, and known risks accurately; and
- credit other contributors and sources appropriately.

Technical disagreement is normal and useful. Maintainers may reject or request changes because of safety, architecture, roadmap scope, test coverage, or maintainability. Disagreement with a decision is not misconduct; harassment, retaliation, or repeatedly disrupting the project after a decision has been explained may be.

## Definitions

For this policy:

- **Provider** means a local or remote storage integration used for backup or synchronization, such as Local Folder, SFTP, or Google Drive.
- **Backup-run content** means the immutable files and manifest belonging to one completed backup run. It is distinct from explicitly mutable provider metadata such as synchronization logs.
- **Roadmap boundaries** are the documented limits of the milestone currently being implemented. They prevent incomplete future features from being activated or presented as ready.
- **Harvested mappings** are save-location suggestions collected from external or automated sources. They are untrusted candidates until reviewed and approved through the project's established workflow.
- **Safety-sensitive change** means a change that can affect user data, authentication, credentials, path containment, overwrite behavior, deletion behavior, or remote storage.

## Project-specific engineering responsibilities

Contributors should preserve the repository's established boundaries:

- `GameSaves.Core` contains provider-neutral models, contracts, and rules and remains free of UI, storage, operating-system, and provider SDK dependencies.
- `GameSaves.Infrastructure` owns filesystem, database, secret-store, network-provider, and Google SDK integrations.
- `GameSaves.App`, the CLI, and Reviewer are presentation surfaces and should use project-owned contracts rather than bypassing Infrastructure boundaries.
- Tests should be deterministic, avoid personal data, and verify safety-sensitive behavior at the appropriate boundary.

Changes must respect the project's data-safety model. In particular:

- backup and synchronization work must not silently delete, move, truncate, or overwrite user data;
- preview, explicit confirmation, copy-only behavior, path containment, and create-only backup content must not be weakened without a clearly reviewed change in project policy;
- mutable provider metadata must remain explicitly separated from immutable backup-run content;
- unavailable providers must not silently fall back to another provider or be presented as implemented;
- harvested or scraped save mappings remain untrusted candidates until reviewed and approved through the established workflow; and
- roadmap boundaries and incomplete features must be represented accurately in code, tests, documentation, and user-facing messages.

Good-faith defects are not conduct violations. Concealing known data-loss risks, fabricating verification results, deliberately bypassing safeguards, or pressuring others to approve unsafe behavior is unacceptable.

## Privacy, credentials, and responsible disclosure

Never place another person's private information or project secrets in issues, pull requests, tests, logs, screenshots, fixtures, commits, or chat. This includes:

- OAuth access or refresh tokens, authorization codes, and client secrets;
- personal account email addresses or other account identifiers;
- Google Drive object or folder IDs when they are not necessary for a sanitized test;
- passwords, private keys, connection credentials, and unredacted configuration;
- private save data, local filesystem paths containing personal information, or screenshots exposing account details; and
- raw provider responses, authorization URLs, or diagnostic output containing sensitive query values.

Use clearly fictional values such as `user@example.invalid` in examples and tests. Keep Google OAuth scope and protected-token behavior consistent with the documented architecture, and never introduce plaintext credential storage.

If sensitive information is exposed, stop redistributing it, notify a maintainer privately, and remove or rotate the affected secret where possible. Do not quote sensitive content in a public reply merely to point out the exposure.

Potential security vulnerabilities, credential leaks, or credible data-loss issues should be reported privately before public technical details are posted. This enables maintainers to investigate without increasing risk to users.

## Unacceptable behavior

Unacceptable behavior includes:

- harassment, intimidation, stalking, threats, or sustained unwanted contact;
- discriminatory, demeaning, or exclusionary language or behavior;
- sexualized language, imagery, or attention;
- insults, personal attacks, trolling, deliberate disruption, or bad-faith review behavior;
- publishing or threatening to publish another person's private information;
- retaliation against someone who raises a safety, security, privacy, or conduct concern;
- impersonation, spam, coordinated harassment, or manipulation of project discussions;
- knowingly publishing credentials, private account data, or exploit details that place users at unnecessary risk;
- deliberately introducing destructive behavior or bypassing the project's safety controls; and
- knowingly misrepresenting test results, live verification, feature readiness, or the effects of a change.

## Reviews and project decisions

Reviewers should distinguish between required changes, suggestions, and personal preferences. Contributors should receive enough context to understand why a safety or architectural requirement matters.

Automated tests are important evidence, but they do not replace review of privacy, data safety, architecture, or scope. Likewise, a failed test or rejected proposal is not a judgment about the contributor. Everyone participating in review should keep the discussion proportionate and professional.

## Maintainer responsibilities

Maintainers are expected to:

- uphold the architectural boundaries and documented safety invariants consistently;
- protect user data, credentials, account information, and sensitive reports;
- review safety-sensitive changes carefully rather than relying only on automated tests;
- explain significant review decisions and distinguish requirements from preferences;
- respond to conduct, privacy, security, and credible data-loss reports as promptly as reasonably possible;
- handle reports impartially and confidentially, involving only the people needed to assess or resolve them; and
- correct or remove content that violates this policy and take proportionate enforcement action when necessary.

Maintainers may reject contributions that conflict with this policy, the project's architecture, roadmap boundaries, or documented safety requirements. They may also edit, hide, lock, or remove harmful content from project spaces.

## Reporting a conduct concern

Report conduct concerns privately to a repository maintainer using a private contact method published on the maintainer's GitHub profile or in the repository settings. Include only the information needed to understand the incident, such as where it occurred, when it occurred, the behavior involved, and any relevant links.

If no private contact method is published, open a minimal issue asking a maintainer to provide a private reporting channel. Do **not** include incident details, personal information, credentials, screenshots, or other sensitive evidence in that public issue.

If a report concerns a maintainer, contact another repository collaborator privately when one is available. Reports will be handled as confidentially as reasonably possible. Information may be shared only with people needed to assess the report, protect participants, or comply with legal obligations.

Reports made in good faith are protected from retaliation. Knowingly false or malicious reports may themselves violate this policy; a report that cannot be substantiated is not automatically a false report.

## Enforcement

Maintainers will consider the context, severity, pattern, impact, and the participant's response when deciding what action is appropriate. Possible actions include:

1. a private clarification or request to correct behavior;
2. a formal warning or required removal of harmful content;
3. a temporary restriction from discussions, reviews, or other project participation;
4. rejection or closure of contributions connected to harmful conduct; or
5. permanent removal from project spaces for severe or repeated violations.

Where practical, a maintainer directly involved in a report should step back from deciding its outcome. Enforcement decisions should protect affected people, avoid unnecessary public disclosure, and be documented privately enough to support consistent treatment.

## Questions and updates

Questions about this policy may be raised in a repository issue when they do not contain private incident details. Maintainers may update this document as the community and project evolve, while preserving its commitments to respectful participation, user safety, privacy, and responsible engineering.
