# AzureTray

A Windows system tray application for managing Azure operations across multiple tenants, with a pluggable extension system.

> **Status: 0.2 — feature-complete enough for daily use, still pre-1.0.** This repository is a clean rewrite of the predecessor `Azure.PIM.Tray` project. Core auth, multi-tenant management, plugins (PIM + LAPS), notifications, and the log viewer all work end-to-end. See [CHANGELOG.md](CHANGELOG.md) for the rolled-up release notes.

## Features

- **Multi-tenant management** with three ways to add a tenant:
  - **Sign in with Windows account** — auto-detects from your signed-in Windows session; zero prompts on Entra-joined boxes.
  - **Sign in with email** — opens the WAM broker picker so you can type any email or pick another work/school account configured in Windows Settings.
  - **Manual setup** — type the Tenant ID + optional app registration name and the app verifies via Graph `/me`.
- **Per-tenant credential** — `InteractiveBrowserCredential` over the WAM broker, MSAL persistent (DPAPI-encrypted) token cache, `AuthenticationRecord` persisted to disk so silent token re-use survives an app restart.
- **Startup sign-in notification** — if a tenant's cached refresh token has expired, a stacked corner toast appears with **Login** / **Disable** buttons instead of an auto-popping broker prompt. Never times out.
- **App registration management** — Settings (admin mode) has per-tenant **Fix permissions** and **✚ Create app registration** buttons. Create provisions a single-tenant public client end-to-end (`POST /applications` + `/servicePrincipals` + WAM redirect URI + admin-consented host + plugin scopes). Fix Permissions uses replace semantics — stale scopes are pruned.
- **Tenant edit** — change display name / client ID via the inline manual form; Tenant ID itself is locked.
- **Plugins** as separate assemblies in `%LOCALAPPDATA%\AzureTray.Data\plugins\`. Two ship in this repo:
  - **PIM** — Entra ID + Azure RBAC PIM approvals (interactive Approve/Reject with justification, eligible-role activation, active-role grayout).
  - **LAPS** — Local Administrator Password Solution password retrieval for Entra-joined devices.
- **Notification stack** — `INotifier` API with `InformationRequest` / `YesNoRequest` / `ChoiceRequest` / `TextInputRequest` types; rendered as frameless bottom-right WPF popups that stack vertically.
- **In-app log viewer** with grouped class dropdown, From/To timestamp filter, type dropdown, and substring search; runtime-controllable log level.
- **Tray menu** with scroll arrows on overflow, hover auto-scroll, and a search box on plugin-provided searchable submenus.
- **Velopack auto-update** with GitHub Releases as the feed; runs as a separate workflow on tag push.

## Building

### Prerequisites

- Windows 10/11
- .NET 10 SDK (`10.0.100`+ — see `global.json`)

### Build

```powershell
dotnet build AzureTray.sln --configuration Release
```

### Test

```powershell
dotnet test AzureTray.sln --configuration Release
```

### Run from source

```powershell
dotnet run --project src\AzureTray\AzureTray.csproj
```

## Configuration

Defaults ship in `src/AzureTray/appsettings.json`. User overrides go in `%APPDATA%\AzureTray\config.json` (created on demand; same JSON shape). The user override is layered on top of the shipped defaults at startup.

| Setting | Default | Notes |
|---|---|---|
| `App:Update:FeedUrl` | empty | Velopack release feed. Empty disables update checks. |
| `App:Logging:MinimumLevel` | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`. |
| `App:Logging:RetainedFileCount` | `14` | Days of rolling log files. |
| `App:AzureCloud:Authority` | public cloud | Override for sovereign clouds. |
| `App:AzureCloud:GraphEndpoint` | public cloud | |
| `App:AzureCloud:ArmEndpoint` | public cloud | |
| `App:Auth:ClientId` | empty | Entra ID app registration client ID used as fallback when a tenant has no dedicated one. Empty falls back to the public Azure CLI client. |
| `App:Auth:RedirectUri` | `http://localhost` | Public-client native redirect URI. |
| `App:Auth:TokenAcquisitionTimeoutSeconds` | `30` | Per-tenant token-acquisition serialization timeout. |
| `App:Auth:AppRegistrationName` | `AzureTray` | Display name the Add Tenant flow auto-discovers via Graph `/applications`. When found, the tenant binds to its appId; when not, the contextual "Create app registration" prompt appears. |
| `App:Plugins:TrustMode` | `AllowUnsigned` | Plugin signature policy. `AllowUnsigned` (dev only — current default), `RequireSigned`, or `RequireTrustedPublisher` (thumbprint allowlist). Tighten for distribution. |

## Paths

The app deliberately stores user data outside the install directory so updates can't wipe it.

| Purpose | Location |
|---|---|
| App install | `%LOCALAPPDATA%\AzureTray\` (Velopack-managed; versioned subfolders inside) |
| Logs | `%LOCALAPPDATA%\AzureTray.Data\logs\app-YYYYMMDD.log` |
| Installed extensions | `%LOCALAPPDATA%\AzureTray.Data\plugins\` |
| Plugin per-instance data | `%LOCALAPPDATA%\AzureTray.Data\plugin-data\<plugin-id>\` |
| `AuthenticationRecord` per tenant | `%LOCALAPPDATA%\AzureTray.Data\auth-records\<tenantId>.bin` (non-secret pointer — tokens stay in the DPAPI-encrypted MSAL cache) |
| User config | `%APPDATA%\AzureTray\config.json` |
| Tenant store | `%APPDATA%\AzureTray\tenants.json` |
| Plugin config | `%APPDATA%\AzureTray\plugin-config.json` |
| Token cache | MSAL-managed per-user store (DPAPI-protected on Windows) |

## Architecture

The current foundation:

- **Generic host + DI** — `Microsoft.Extensions.Hosting` boots before WPF. Every service is resolved from the container.
- **Logging** — Serilog with rolling file sink, debug sink, in-memory ring-buffer sink (for the in-app log viewer), and a runtime-controllable `LoggingLevelSwitch`.
- **Config** — `IConfiguration` layered from `appsettings.json` (shipped) + `%APPDATA%\AzureTray\config.json` (user override) + environment variables. Bound to typed `*Options` records.
- **HTTP** — `IHttpClientFactory` with named clients (`graph`, `arm`) and the standard resilience handler (retry, circuit breaker, timeout, 429 backoff).
- **Auth** — single `ICredentialFactory` builds `InteractiveBrowserCredential` per tenant with MSAL persistent token cache, wrapped in a per-tenant `SerializedTokenCredential` (configurable timeout, one stuck tenant cannot block others).
- **MVVM** — `CommunityToolkit.Mvvm` source generators. ViewModels know nothing about WPF.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). All PRs run a build + test gate on Windows before they can merge.

## Security

See [SECURITY.md](SECURITY.md) for the vulnerability reporting policy.

## License

[MIT](LICENSE).
