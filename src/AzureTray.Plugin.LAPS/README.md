# AzureTray.Plugin.LAPS

LAPS (Local Administrator Password Solution) plugin for [AzureTray](https://github.com/Proxylayer/AzureTray). Retrieve Windows LAPS passwords for Entra-joined devices from the system tray, then copy them to the clipboard.

## What it does

- Tray menu lists Entra-joined devices for which the signed-in user has the right to read the local admin credential, grouped by tenant.
- Selecting a device fetches its current local admin password via Microsoft Graph and copies it to the clipboard.
- Devices the user isn't entitled to read are silently filtered.

## Required permission

| API | Scope | Reason |
|---|---|---|
| Microsoft Graph | `DeviceLocalCredential.Read.All` | Read Windows LAPS device credentials |

Use **Settings -> Fix permissions** after installing to grant admin consent on each tenant where the plugin should operate. Note that this scope is admin-consent-only — a tenant administrator must approve it.

## Install

Open **Settings -> Browse online plugins** in AzureTray, find "LAPS Passwords", and click Install. The host verifies the package against the GitHub Advisory Database before downloading.

## Source

[github.com/Proxylayer/AzureTray](https://github.com/Proxylayer/AzureTray) — same repo as the host. Issues, PRs, and feature requests welcome.
