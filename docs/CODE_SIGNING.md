# Code signing policy

Windows signing with SignPath Foundation is being prepared. Juvin has not yet
been accepted into the Foundation program, and existing Windows GitHub releases
are unsigned. No Microsoft Store distribution is part of this setup.

The proposed arrangement, subject to Foundation approval, is:
**Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).** The Foundation is the certificate's
publisher. This statement describes the requested service, not an active certificate.

## People and approvals

- Author, committer, and reviewer: [Chris Smashe (`csmashe`)](https://github.com/csmashe).
- Proposed signing approver: Chris Smashe. SignPath must approve the actual policy.
- Contributors' changes, including build scripts and workflows, require maintainer
  review before merging. Maintainers must use MFA on GitHub and SignPath.
- Each release signing request requires manual approval in SignPath. This approval
  must not be bypassed or replaced with an unattended policy during onboarding.

## What is signed

GitHub-hosted runners build the Windows application from this repository.
Only Juvin's own executables and assemblies, its generated Inno Setup uninstaller,
and the final installer are submitted. Third-party DLLs retain their original bytes
and signatures; they are not signed under Juvin's certificate.

The [artifact configuration](../scripts/windows/signpath/artifact-configuration.xml)
restricts product name, company, and product version. The build pins the expected
certificate, verifies signatures before using signed files, and requires timestamps
and Windows trust for production signatures. Test-signed files cannot be attached
to releases by the workflow. A signing failure fails packaging; it never produces
an unsigned replacement release.

The signing credential exists only in the protected `signpath` GitHub environment,
restricted to the `main` branch. PRs, feature branches, tags, and self-hosted runners
cannot use it. See [setup and release instructions](WINDOWS_SIGNING.md).

## Privacy

See the [privacy policy](PRIVACY.md). Email content goes to the model provider the
user selects. Optional, scrubbed Sentry crash reports are enabled by default in
builds configured with a DSN and can be disabled in first-run setup or Privacy and
data. The Foundation application must disclose this behavior and confirm whether
the existing first-run control satisfies its installation requirements before
production signing is activated. Do not claim that all network transfers require
an explicit per-request user action.
