# AzureTray.Plugin.PIM

PIM (Privileged Identity Management) plugin for [AzureTray](https://github.com/Proxylayer/AzureTray). Approve and reject Entra ID + Azure RBAC PIM requests from the system tray, and activate eligible roles in one click.

## What it does

- Tray menu lists pending Entra ID and Azure RBAC PIM approvals, grouped by tenant. Approving or rejecting from the menu calls the Graph / ARM PIM API directly.
- Eligible roles surface as one-click activation entries. The plugin handles MFA challenge replay, ticket-number prompts, and justification text per the tenant's PIM policy.
- Activation durations are clamped to each role's PIM policy maximum, so the prompt only ever offers durations the policy will accept (a role capped at 2 hours offers `1 hour` and `2 hours`). Roles whose cap is tighter than the usual maximum say so on the menu row (`Reader  (Dev sub)  ·  max 2h`). The cap is read once per poll per tenant and cached; if the policy cannot be read (it requires a directory role such as Global Reader or Security Reader), Entra roles fall back to the service's 8-hour ceiling and Azure RBAC roles to the standard 1/4/8-hour list.
- Active roles are visually distinguished so you don't accidentally re-activate, and each one shows how long the activation has left (`✓ active · 3h 42m left`), recomputed every time the menu opens. Right-click an active role to **Deactivate** it (or copy its name); right-click any role to copy its name.
- Activations that need an approver are tracked until the decision lands — including across app restarts. On approval the plugin refreshes the tenant's access token so the new role takes effect immediately instead of when the cached token expires, then updates the menu and tells you the role is live.
- A badge on the tray icon reflects total pending approvals across all managed tenants, with a tooltip summarising the count.

## Required permissions

The plugin asks the host to ensure these delegated scopes on the app registration in every managed tenant. Use **Settings -> Fix permissions** after installing to grant admin consent.

| API | Scope | Reason |
|---|---|---|
| Microsoft Graph | `User.Read` | Sign in and read the user profile |
| Microsoft Graph | `RoleAssignmentSchedule.ReadWrite.Directory` | Submit self-activation requests for Entra ID roles |
| Microsoft Graph | `RoleEligibilitySchedule.Read.Directory` | List eligible and currently active Entra ID role assignments |
| Microsoft Graph | `PrivilegedAccess.ReadWrite.AzureAD` | List, fetch, and approve Entra ID PIM approval requests |
| Microsoft Graph | `RoleManagement.Read.Directory` | Read PIM role management policies (approval requirement, maximum activation duration) and poll activation request status |
| Azure Resource Manager | `user_impersonation` | All Azure RBAC PIM operations on subscriptions and resources |

## Install

Open **Settings -> Browse online plugins** in AzureTray, find "PIM Approvals", and click Install. The host verifies the package against the GitHub Advisory Database before downloading.

## Source

[github.com/Proxylayer/AzureTray](https://github.com/Proxylayer/AzureTray) — same repo as the host. Issues, PRs, and feature requests welcome.
