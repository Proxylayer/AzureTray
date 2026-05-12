# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Use GitHub's [private vulnerability reporting](https://github.com/Proxylayer/AzureTray/security/advisories/new) to send a report. You should receive an initial acknowledgement within a few working days.

When reporting, include:

- A description of the issue and its impact
- Reproduction steps, ideally with a minimal test case
- The version (release tag or commit SHA) you observed it on
- Any suggested mitigation if you have one

## What is in scope

- Authentication, token handling, and credential storage flows
- Extension loading and the plugin trust boundary
- Update flow integrity (Velopack package authenticity)
- Persistence of secrets or tokens to disk

## What is out of scope

- Vulnerabilities in third-party dependencies that are already publicly disclosed (open a regular issue with the CVE)
- Issues that require physical access to a logged-in user's machine
- Self-XSS or attacks that require the user to copy-paste malicious input

## Disclosure

This project follows coordinated disclosure. Once a fix is ready, the advisory will be published with credit to the reporter (unless they opt out) and a CVE if one applies.

## Release integrity

Releases are **not** Authenticode-signed at this stage. SmartScreen will warn on first download until Velopack's installer earns reputation. To verify a release is the one published by this repo:

- Compare the binary's SHA-256 to the one in the release notes.
- Verify the GitHub-issued build-provenance attestation:

```powershell
gh attestation verify AzureTray.exe --repo Proxylayer/AzureTray
```

Attestations are generated for every release via [actions/attest-build-provenance](https://github.com/actions/attest-build-provenance) and tie the binary back to the exact workflow run + commit SHA that produced it.
