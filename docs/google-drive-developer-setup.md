# Google Drive Developer Setup

This guide is for developers working on Game Save Manager. Normal end users do not need to create a Google Cloud project. Completing these steps prepares a development project only: it does not make Google Drive sync functional. OAuth login and Google Drive integration begin in later roadmap milestones.

Milestone H installs the official Google authentication and Drive client packages in `GameSaves.Infrastructure` only. The application still does not read `GAMESAVES_GOOGLE_CLIENT_ID`, perform OAuth login, load a downloaded credential file, store a Google token, or call the Drive API. Builds and tests require no personal Google configuration.

Never use or commit a personal OAuth token for repository development, and never send a Google Account password to the application. A later milestone must use Google's supported browser-based OAuth flow for installed desktop applications.

## Prerequisites

You need:

- a Google Account with access to Google Cloud Console;
- a Google Drive-enabled account that you are authorized to use for testing;
- a local checkout of Game Save Manager;
- a developer contact email that you actively monitor; and
- an understanding that this is a development project, not a production OAuth application.

Use a separate Cloud project for development and testing instead of reusing a future production project. A suitable project name is `Game Save Manager Development`. The OAuth app name should accurately represent this project and must not imply ownership or endorsement by Google.

Use placeholders in notes, examples, issues, and pull requests:

```text
YOUR_GOOGLE_CLOUD_PROJECT_ID
YOUR_DEVELOPER_EMAIL
YOUR_TEST_GOOGLE_ACCOUNT
YOUR_DESKTOP_CLIENT_ID
```

Do not commit a real project ID, account identifier, or credential.

## Create the development project

Follow Google's current [Create projects](https://docs.cloud.google.com/resource-manager/docs/creating-managing-projects) guidance:

1. Open [Google Cloud Console](https://console.cloud.google.com/).
2. Open the project selector in the console toolbar.
3. Select **New project**.
4. Enter `Game Save Manager Development`, or another clearly development-specific name.
5. Select the appropriate organization or folder when the account belongs to one. A personal account can use **No organization** when that choice is available.
6. Create the project.
7. Wait for creation to finish, then confirm that the new project is selected.
8. Record the project name in private developer notes.
9. Keep its actual project ID out of committed examples.

> **Always verify the selected Google Cloud project before enabling APIs or creating OAuth clients.**

All later API, consent, audience, scope, and client settings must be configured under this same selected project.

## Enable only the Google Drive API

Follow Google's current [API enablement](https://docs.cloud.google.com/apis/docs/getting-started#enabling_apis) flow:

1. Confirm the development project is selected.
2. Open **APIs & Services**.
3. Open **Library**.
4. Search for **Google Drive API**.
5. Select **Google Drive API**.
6. Select **Enable**.
7. Confirm it appears among the project's enabled APIs.

Do not enable Google Picker, Sheets, Docs, People, Gmail, service-account APIs, or any unrelated API for this milestone. Add another API only when a later milestone has a concrete requirement.

## Configure Google Auth Platform

Google's current configuration is organized under **Google Auth Platform**, including **Branding**, **Audience**, **Data Access**, **Clients**, and **Verification Center**. If the platform is not configured yet, **Branding** presents a **Get Started** flow. Google's [OAuth consent configuration guide](https://developers.google.com/workspace/guides/configure-oauth-consent) is the authority if console labels change.

### Branding and contact information

1. Open **Google Auth Platform** > **Branding**.
2. Select **Get Started** when prompted.
3. Set **App name** to `Game Save Manager`.
4. Select a monitored **User support email**.
5. In **Contact Information**, enter `YOUR_DEVELOPER_EMAIL` using an address you monitor.
6. Review and accept Google's API Services User Data Policy when prompted, then create the configuration.

The initial setup requires the app name, user support email, audience, and developer contact information. An app logo, homepage, privacy-policy URL, and terms-of-service URL can remain unset during private development when the console does not require them. Do not invent URLs. A production or verification submission has additional branding, verified-domain, homepage, and policy requirements; preparing or submitting that material is outside Milestone G.

Google can send OAuth policy, verification, configuration, and security notices to the registered contact addresses. Keep them current and monitored.

### Audience

The repository's default development setup is:

```text
External + Testing
```

Use **External** for a personal account or a public-development project, and leave the publishing status in **Testing**. An eligible Google Workspace project owned by a Cloud organization may offer an **Internal** audience, limited to that organization.

Changing the application to Production and completing OAuth verification are not part of this milestone. Do not use **Verification Center** to submit a production application as part of these instructions.

### Development test users

For an External application in Testing:

1. Open **Google Auth Platform**.
2. Open **Audience**.
3. Find **Test users**.
4. Select **Add users**.
5. Add `YOUR_TEST_GOOGLE_ACCOUNT` and, only when needed, `SECOND_TEST_ACCOUNT_IF_NEEDED`.
6. Save the configuration.
7. Verify that each intended test account appears in the list.

Only add people who have agreed to test the application. Never commit test-user email addresses, passwords, shared plaintext credentials, or account screenshots. Contributors should use their own explicitly authorized test account, not a maintainer's personal account.

## Declare the planned scope

The roadmap plans this scope:

```text
https://www.googleapis.com/auth/drive.file
```

Google describes `drive.file` as per-file access for files the application creates or files the user makes available to it. It is narrower than full Drive access and is listed as non-sensitive in Google's [Drive scope reference](https://developers.google.com/workspace/drive/api/guides/api-specific-auth).

In **Google Auth Platform** > **Data Access**, use **Add or Remove Scopes** to review or declare this scope for the development configuration. Google requires scopes to be declared in the console and requested by application code. Milestone G documents the intended scope only; it adds no code that requests it.

Do not configure these broader scopes unless a later milestone demonstrates that they are unavoidable:

```text
https://www.googleapis.com/auth/drive
https://www.googleapis.com/auth/drive.readonly
```

Full Drive access is not planned.

## Create the Windows desktop OAuth client

Follow Google's current [OAuth client credential](https://developers.google.com/workspace/guides/create-credentials#desktop-app) instructions:

1. Confirm the correct development project is selected.
2. Open **Google Auth Platform**.
3. Open **Clients**.
4. Select **Create client**.
5. Choose **Desktop app**.
6. Name it `Game Save Manager Desktop Development`.
7. Create the client.
8. Record the generated Client ID in private local configuration.
9. Download the JSON only if it is needed for local development.
10. Store any downloaded file outside the repository.

Do not create Web application, Android, iOS, Chrome extension, or service-account credentials for this milestone. Google recommends a separate client ID for each platform; this guide covers the current Windows desktop development client only.

### Installed-app security model

Google's [OAuth guide for desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app) states that installed applications cannot keep embedded secrets confidential. A desktop client secret is not an application password and must never be used as proof that a request came from an authentic copy of Game Save Manager.

A later implementation must rely on the supported installed-app flow, system-browser authorization, PKCE where applicable, redirect and state validation, and secure token storage. Account access and refresh tokens are real secrets and must use the existing secure secret store.

Repository policy is deliberately stricter than the installed-client confidentiality model:

- never commit downloaded OAuth credential JSON;
- never commit a developer client secret;
- never use a personal development Client ID in committed examples;
- never commit user tokens; and
- never commit test-user account information.

## Planned local Client ID configuration

The future application will read the development Client ID from:

```text
GAMESAVES_GOOGLE_CLIENT_ID
```

Set it for the current PowerShell session:

```powershell
$env:GAMESAVES_GOOGLE_CLIENT_ID = "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com"
```

Or set a persistent Windows user environment variable:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_GOOGLE_CLIENT_ID",
    "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com",
    "User")
```

A new terminal or App process might be required after setting a persistent variable.

> Reading this environment variable is implemented in a later milestone. Milestone G establishes and documents the configuration name only.

There is no committed default Client ID and no client-secret environment variable. Do not add a real Client ID to source code, project files, README, JSON, tests, CI, screenshots, or error messages.

## Handle downloaded credential files

If Google offers a downloaded OAuth client JSON:

1. Download it only to a local temporary or developer configuration directory.
2. Never place it anywhere in the repository working tree.
3. Never rename it into a committed appsettings file.
4. Never send it through an issue, pull request, chat, screenshot, or CI log.
5. Delete unused copies.
6. Treat the Client ID as configuration.
7. Do not treat the desktop client secret as a reliable confidential credential.
8. Never store user OAuth tokens in the downloaded client file.

The recommended local directory is outside the repository:

```text
%LOCALAPPDATA%\GameSave\Developer\GoogleOAuth\
%LOCALAPPDATA%\GameSave\Developer\GoogleOAuth\desktop-client.json
```

Later application token data belongs in the existing secure secret store, not beside this file.

## Repository ignore protections

The project-specific `.gitignore` section blocks common downloaded and developer-local file locations:

- `**/client_secret_*.json` protects Google's common downloaded client filename pattern anywhere in the working tree.
- `Manager/GameSaves.App/credentials.json` protects a common but disallowed credential filename.
- `Manager/GameSaves.App/google-oauth-client.local.json` protects a local desktop-client configuration.
- `Manager/GameSaves.App/google-drive.local.json` protects a local Drive configuration.
- `Manager/GameSaves.App/.google-oauth/` protects local OAuth working data.
- `Manager/GameSaves.App/google-oauth-token-cache/` protects a local token-cache directory.

The preferred workflow is still to store OAuth files outside the repository. Ignore rules are only a second line of defence and do not make a credential safe.

## If sensitive data is exposed

If a credential, token, or personal account value is accidentally committed:

1. Stop using the exposed credential or token.
2. Revoke affected user OAuth tokens where applicable.
3. Delete or rotate the affected OAuth client when appropriate.
4. Remove the file from the current Git tree.
5. Treat it as exposed even if the commit was quickly reverted.
6. Review repository history and any remote forks or copies.
7. Do not assume adding the path to `.gitignore` repairs the exposure.
8. Never paste the exposed value into an issue while asking for help.

Rewriting Git history can reduce continued exposure, but it does not revoke a credential or token.

## Before committing Google Drive work

- [ ] No `client_secret_*.json` file is tracked
- [ ] No downloaded OAuth credential JSON is tracked
- [ ] No access token is present
- [ ] No refresh token is present
- [ ] No personal Google account email is present
- [ ] No personal Google Drive folder ID is present
- [ ] Examples contain placeholders only
- [ ] Local OAuth files are ignored
- [ ] `git diff` contains no credentials
- [ ] `git status --ignored` shows local OAuth files as ignored

Automated checks reduce risk but do not replace manual review.

## Official references

- [Create and manage Google Cloud projects](https://docs.cloud.google.com/resource-manager/docs/creating-managing-projects)
- [Enable Google Cloud APIs](https://docs.cloud.google.com/apis/docs/getting-started#enabling_apis)
- [Configure OAuth consent and scopes](https://developers.google.com/workspace/guides/configure-oauth-consent)
- [Create a desktop OAuth client](https://developers.google.com/workspace/guides/create-credentials#desktop-app)
- [OAuth 2.0 for desktop applications](https://developers.google.com/identity/protocols/oauth2/native-app)
- [OAuth authorization best practices](https://developers.google.com/identity/protocols/oauth2/resources/best-practices)
- [Choose Google Drive API scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
