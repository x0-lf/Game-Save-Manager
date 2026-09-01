# Google Drive Developer Setup

> **Developer-only and security-sensitive.** Normal users do not create a
> Google Cloud project or supply a personal OAuth client. This guide configures
> a private development environment only. Never commit credentials, tokens,
> account identifiers, screenshots, authorization URLs, or Drive object IDs.

Current provider behavior and limitations belong to the
[sync provider guide](sync-providers.md). Closed live evidence belongs to the
[historical acceptance archive](history/google-drive-acceptance.md).

## Prerequisites

- a Google account and Drive account you are authorized to use for testing;
- access to Google Cloud Console;
- a separate development Cloud project;
- a monitored developer contact address; and
- a Windows desktop environment capable of running the App and system browser.

Use an isolated test account and synthetic backup runs. Do not use another
maintainer's account, production data, or irreplaceable saves.

## Create the Cloud project

Follow Google's official [project creation guidance](https://cloud.google.com/resource-manager/docs/creating-managing-projects):

1. Open Google Cloud Console and select **New project**.
2. Name it clearly for development, such as `Game Save Manager Development`.
3. Select the intended organization or **No organization**, when available.
4. Create the project and confirm it remains selected before every later task.

Keep the generated project ID in private developer notes. Do not add it to the
repository, an issue, a pull request, a screenshot, or test output.

## Enable the Drive API

1. In the selected project, open **APIs & Services** > **Library**.
2. Find **Google Drive API** and enable it.
3. Confirm the API is enabled for the same development project.

Do not enable Picker, Sheets, Docs, People, Gmail, service-account APIs, or
other APIs without a separate implemented requirement.

## Configure consent and audience

Use Google's current [OAuth consent configuration guidance](https://developers.google.com/workspace/guides/configure-oauth-consent):

1. Open **Google Auth Platform** in the selected project.
2. Under **Branding**, set the app name to `Game Save Manager`, choose a
   monitored support address, and enter a monitored developer contact address.
3. Under **Audience**, use **External** and keep the app in **Testing** for a
   personal or public-development project. An eligible organization may use
   **Internal** only for its own users.
4. For an External test app, add only developers who explicitly agreed to test.
5. Do not submit a production verification request as part of local setup.

Console labels can change; Google's documentation is authoritative when they do.
Do not invent a homepage, privacy URL, or terms URL to satisfy optional fields.

## Declare the exact scope

Under **Google Auth Platform** > **Data Access**, declare exactly:

```text
https://www.googleapis.com/auth/drive.file
```

The App requests that scope and no Google, OpenID, email, or profile scope.
Do not substitute full Drive or Drive-readonly access. `drive.file` limits the
App to files it creates or the user makes available to it and is the reason the
App does not provide an arbitrary browser across My Drive.

## Create the desktop client

Follow Google's official [OAuth client guidance](https://developers.google.com/workspace/guides/create-credentials#desktop-app):

1. Open **Google Auth Platform** > **Clients**.
2. Create an OAuth client of type **Desktop app**.
3. Give it a development-specific name.
4. Copy the Client ID into private local configuration.
5. Download the JSON only if needed, and store it outside the repository.

Do not create a Web, Android, iOS, browser-extension, or service-account client
for this desktop flow. Installed applications cannot keep an embedded client
secret confidential; authentication security comes from the supported browser
flow, loopback callback, state validation, PKCE, and protected user tokens.

## Configure the App locally

For the current PowerShell process:

```powershell
$env:GAMESAVES_GOOGLE_CLIENT_ID = "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com"
```

Or store it as a Windows user environment variable:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_GOOGLE_CLIENT_ID",
    "YOUR_DESKTOP_CLIENT_ID.apps.googleusercontent.com",
    "User")
```

The process value takes precedence. The App can also read the persistent user
value directly. Restart the App after changing either value.

Google documents the desktop client secret as optional, but the generated
client and selected .NET authorization path can require it for token exchange.
Configure only the value from the same desktop client when the exchange reports
that it is required:

```powershell
[Environment]::SetEnvironmentVariable(
    "GAMESAVES_GOOGLE_CLIENT_SECRET",
    "YOUR_DESKTOP_CLIENT_SECRET",
    "User")
```

The optional value is passed only to installed-app authorization. Neither value
is copied into profile JSON, ordinary SQLite fields, logs, result formatting,
or the UI. Access and refresh tokens use the protected secret store.

## Ignored-file and review rules

Store downloaded or local configuration outside the repository. The following
patterns are ignored as a second line of defense:

```text
**/client_secret_*.json
Manager/GameSaves.App/credentials.json
Manager/GameSaves.App/google-oauth-client.local.json
Manager/GameSaves.App/google-drive.local.json
Manager/GameSaves.App/.google-oauth/
Manager/GameSaves.App/google-oauth-token-cache/
*.env
```

Ignore rules do not make a credential safe to share. Before every commit or
pull request, inspect the diff and status for client values, downloaded JSON,
tokens, account names, email addresses, authorization URLs, private paths,
screenshots, and Drive IDs.

## Sanitized end-to-end smoke test

This test requires Windows, a desktop display, network access, the system
browser, and an explicitly authorized development account.

1. Create a saved Google Drive profile with a generic display name.
2. Choose **Connect Google Drive** and complete consent in the system browser.
   Verify the consent screen requests only `drive.file`.
3. Set up the application root. Confirm the App reports it ready and creates or
   reuses `My Drive/GameSave Manager Backups` without an arbitrary folder picker.
4. Restart the App. Select the same profile and confirm authentication restores
   without opening the browser.
5. Create a small synthetic local backup run and preview Sync. Confirm it is an upload.
6. Execute the selected upload, then preview again and confirm the run is in sync.
7. On a clean synthetic local backup base, preview and download the same run.
   Confirm it appears in Backups and passes restore preview.
8. Create a controlled same-name/different-manifest case. Confirm Sync reports a
   conflict and copies neither side.
9. Start a controlled larger upload and cancel it. Confirm no manifest presents
   the incomplete remote folder as a complete run.
10. Disconnect. Confirm the local token and displayed account identity clear,
    while the saved profile, root metadata, local backups, history, and Drive
    content remain.

Record only generic stages, pass/fail, stable sanitized error codes, and whether
unexpected mutation occurred. Do not record account values, client values,
folder IDs, authorization URLs, raw provider responses, or screenshots.

Real quota exhaustion and forced network loss are not safe routine smoke-test
steps. Their deterministic coverage and live limitation are documented in the
[provider guide](sync-providers.md).

## Incident response

If any client secret, token, authorization code, downloaded credential file,
test-user identity, Drive ID, or private screenshot enters a commit, issue,
artifact, or shared log:

1. stop using and sharing the exposed value;
2. revoke affected grants or tokens and rotate the OAuth client value when applicable;
3. remove the data from the working tree and published artifacts;
4. inspect history, forks, CI logs, caches, screenshots, and local databases for copies;
5. notify maintainers through the private process in [SECURITY.md](../SECURITY.md);
6. add the narrowest ignore or regression guard that would prevent recurrence; and
7. remember that deleting or rewriting a commit does not revoke a credential.

Never paste the exposed value into a follow-up message to prove that it existed.
