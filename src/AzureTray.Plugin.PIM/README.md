# AzureTray.Plugin.PIM

PIM (Privileged Identity Management) plugin for [AzureTray](https://github.com/Proxylayer/AzureTray). Approve and reject Entra ID, Azure RBAC, and PIM for Groups requests from the system tray, and activate eligible roles and group access in one click.

## What it does

- Tray menu lists pending Entra ID, Azure RBAC, and PIM for Groups approvals, grouped by tenant. Approving or rejecting from the menu calls the Graph / ARM PIM API directly. When another approver decides first, the plugin says so plainly instead of reporting a failure.
- Eligible roles surface as one-click activation entries. The plugin handles MFA challenge replay, ticket-number prompts, and justification text per the tenant's PIM policy.
- Activation durations are clamped to each role's PIM policy maximum, so the prompt only ever offers durations the policy will accept (a role capped at 2 hours offers `1 hour` and `2 hours`). Roles whose cap is tighter than the usual maximum say so on the menu row (`Reader  (Dev sub)  ·  max 2h`). The cap is read once per poll per tenant and cached; if the policy cannot be read (it requires a directory role such as Global Reader or Security Reader), Entra roles fall back to the service's 8-hour ceiling and Azure RBAC roles to the standard 1/4/8-hour list.
- Active roles are visually distinguished so you don't accidentally re-activate, and each one shows how long the activation has left (`✓ active · 3h 42m left`), recomputed every time the menu opens. Right-click an active role to **Deactivate** it (or copy its name); right-click any role to copy its name.
- Activations that need an approver are tracked until the decision lands — including across app restarts. On approval the plugin refreshes the tenant's access token so the new role takes effect immediately instead of when the cached token expires, then updates the menu and tells you the role is live.
- **PIM for Groups** is a third source, listed under an **Entra Groups** heading after Entra ID and Azure RBAC. Eligible membership and ownership of PIM-onboarded groups activate, deactivate, gray out when active, and go through approval exactly like a role does — a row reads `Member (Contoso SQL Admins)`, with the access type in the role slot and the group in the scope slot. Activation caps come from each group's own member/owner policy, falling back to the service's 8-hour ceiling when the policy cannot be read. A group whose display name Graph will not hand over shows its object id rather than disappearing from the list.
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
| Microsoft Graph | `PrivilegedEligibilitySchedule.Read.AzureADGroup` | List eligible PIM group memberships and ownerships |
| Microsoft Graph | `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` | Activate and deactivate group membership, read active group access and request status, and approve or deny group activation requests |
| Microsoft Graph | `RoleManagementPolicy.Read.AzureADGroup` | Read PIM for Groups activation policies (maximum duration, approval requirement) |
| Azure Resource Manager | `user_impersonation` | All Azure RBAC PIM operations on subscriptions and resources |

## Install

Open **Settings -> Browse online plugins** in AzureTray, find "PIM Approvals", and click Install. The host verifies the package against the GitHub Advisory Database before downloading.

## Source

[github.com/Proxylayer/AzureTray](https://github.com/Proxylayer/AzureTray) — same repo as the host. Issues, PRs, and feature requests welcome.
