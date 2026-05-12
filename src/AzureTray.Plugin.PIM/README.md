# AzureTray.Plugin.PIM

PIM (Privileged Identity Management) plugin for [AzureTray](https://github.com/Proxylayer/AzureTray). Approve and reject Entra ID + Azure RBAC PIM requests from the system tray, and activate eligible roles in one click.

## What it does

- Tray menu lists pending Entra ID and Azure RBAC PIM approvals, grouped by tenant. Approving or rejecting from the menu calls the Graph / ARM PIM API directly.
- Eligible roles surface as one-click activation entries. The plugin handles MFA challenge replay, ticket-number prompts, and justification text per the tenant's PIM policy.
- Active roles are visually distinguished so you don't accidentally re-activate.
- A badge on the tray icon reflects total pending approvals across all managed tenants.

## Required permissions

The plugin asks the host to ensure these delegated scopes on the app registration in every managed tenant. Use **Settings -> Fix permissions** after installing to grant admin consent.

| API | Scope | Reason |
|---|---|---|
| Microsoft Graph | `User.Read` | Sign in and read the user profile |
| Microsoft Graph | `RoleAssignmentSchedule.ReadWrite.Directory` | Submit self-activation requests for Entra ID roles |
| Microsoft Graph | `RoleEligibilitySchedule.Read.Directory` | List eligible and currently active Entra ID role assignments |
| Microsoft Graph | `PrivilegedAccess.ReadWrite.AzureAD` | List, fetch, and approve Entra ID PIM approval requests |
| Microsoft Graph | `RoleManagement.Read.Directory` | Read PIM policies and poll activation request status |
| Azure Resource Manager | `user_impersonation` | All Azure RBAC PIM operations on subscriptions and resources |

## Install

Open **Settings -> Browse online plugins** in AzureTray, find "PIM Approvals", and click Install. The host verifies the package against the GitHub Advisory Database before downloading.

## Source

[github.com/Proxylayer/AzureTray](https://github.com/Proxylayer/AzureTray) — same repo as the host. Issues, PRs, and feature requests welcome.
