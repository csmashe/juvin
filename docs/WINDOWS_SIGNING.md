# Windows signing with SignPath Foundation

## Current state

The integration is prepared; a Foundation account, approved project, certificates,
and signing token are still required. Existing releases remain unsigned during
onboarding. This does not create a paid subscription or add Microsoft Store packaging.

Read the [code signing policy](CODE_SIGNING.md) and
[Foundation conditions](https://signpath.org/terms.html).

## Application

Apply at <https://signpath.org/apply> for **Juvin**, repository
<https://github.com/csmashe/juvin>, maintained by Chris Smashe (`csmashe`).
Juvin is GPL-3.0-only, actively maintained, and publishes Windows installers alongside
Linux and macOS releases. It rewrites a user-selected Gmail or Outlook draft through
a browser extension and a local desktop app. Local mode uses a model server chosen
by the user; optional Codex mode uses the user's Codex installation/account.

Provide these links after this change is on main:

- Policy: <https://github.com/csmashe/juvin/blob/main/docs/CODE_SIGNING.md>
- Privacy: <https://github.com/csmashe/juvin/blob/main/docs/PRIVACY.md>
- Releases: <https://github.com/csmashe/juvin/releases>
- Workflow: <https://github.com/csmashe/juvin/blob/main/.github/workflows/release-windows.yml>
- Artifact XML: <https://github.com/csmashe/juvin/blob/main/scripts/windows/signpath/artifact-configuration.xml>

Disclose the default-on, optional Sentry crash reports and the first-run opt-out.
Ask whether the Foundation also requires an opt-out in the Inno installer itself;
the current installer has none. Resolve this before enabling production signing.
Foundation acceptance also depends on its assessment of the project's reputation.
The user must confirm contact details, MFA, and acceptance of the provider's terms.

## Account and credential setup

1. Have SignPath approve the project and the checked-in artifact configuration.
   Do not reuse another project's organization ID, token, or certificate.
2. Install the official [SignPath GitHub App](https://github.com/apps/signpath)
   with access limited to `csmashe/juvin`, and configure GitHub.com as its trusted
   build system. All upstream build/sign jobs use GitHub-hosted runners.
3. Create GitHub environment **signpath**. Use selected deployment branches with
   only the branch **main** allowed; no tag rules, wildcards, or feature branches.
   Preserve any additional review requirements. A workflow `if` is not a substitute
   for this server-side branch restriction.
4. Store **SIGNPATH_API_TOKEN** only as a secret in that environment. Never create
   a repository/organization copy available to this repository. `unsigned-windows`
   must contain no signing credentials. Never put a token in code, build arguments,
   artifacts, or logs.
5. Set these nonsecret repository variables from the approved SignPath setup:

   | Variable | Value |
   | --- | --- |
   | `SIGNPATH_ORGANIZATION_ID` | Assigned Foundation organization ID |
   | `SIGNPATH_PROJECT_SLUG` | Assigned project slug |
   | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Approved configuration slug |
   | `SIGNPATH_TEST_CERTIFICATE_THUMBPRINT` | Test certificate's 40 hex SHA-1 identifier |
   | `SIGNPATH_CERTIFICATE_THUMBPRINT` | Production certificate's 40 hex SHA-1 identifier |

The thumbprint identifies the certificate; file signatures use SHA-256.
The XML parameter `version` is the binaries' complete informational/product version,
including the source revision. Packaging gives Inno the same value as the app.

## Test onboarding

Run **Release Windows** manually from `main` with `signing_policy=test-signing` and
`publish_signed_release=false`. Approve its three SignPath requests: owned payload,
generated uninstaller, and installer. Each waits at most 30 minutes. Test signatures
must match the configured test certificate and have valid integrity; only that
certificate's expected untrusted-root error is allowed. The test certificate is not
added to Trusted Roots. Never distribute test-signed artifacts to users.

Artifacts include the signing policy in their names. Request links appear in the
job summary. Source actions are pinned, and the artifact upload action is run inside
a JavaScript action so GitHub artifact credentials are available to Inno's callbacks.
These callbacks are part of the same GitHub-hosted job; they do not use local builds
or an unverified artifact-download URL.

## Production releases

After Foundation approval, review the privacy requirements, enable the assigned
`release-signing` policy, set its certificate pin, and first run with publication off.
Verify signatures and timestamps and perform a real downloaded-installer check on
Windows. Signing identifies the publisher; SmartScreen reputation can still take time.

Then set repository variable **SIGNPATH_SIGNING_POLICY=release-signing**. From then
on a pushed tag does not build or publish an unsigned Windows installer. Linux/macOS
tag workflows still create their usual draft release. For Windows:

1. Create the version tag at the exact current main commit.
2. Run **Release Windows** from `main`, enter that version, choose `release-signing`,
   and select `publish_signed_release`.
3. Approve the three signing requests in SignPath as they arrive.
4. Packaging verifies signatures and runs the install/uninstall smoke test before
   attaching the installer to the matching draft release.

If main has advanced past the tag, publication fails. This avoids signing one commit
and attaching it to another. Published release assets are never replaced. Keep the
release draft until its platform checks are complete.
