# Contributing to AzureTray

Thanks for your interest. This project is a refactor in progress and contributions are welcome.

## Prerequisites

- Windows 10/11 (the app uses WPF + the Win32 tray API)
- .NET 10 SDK at the version pinned in `global.json`

## Local development

```powershell
git clone https://github.com/Proxylayer/AzureTray
cd AzureTray
dotnet restore
dotnet build AzureTray.sln --configuration Release
dotnet test  AzureTray.sln --configuration Release
```

`TreatWarningsAsErrors` is on by default. If a build fails on a warning, fix the warning — do not suppress globally without a reason note in `.editorconfig`.

## Workflow

1. Create a feature branch off `main` (`feat/...`, `fix/...`, `refactor/...`, `docs/...`).
2. Keep changes focused. One concern per PR. Smaller PRs are reviewed faster.
3. Write or update tests for the behaviour you change. New public types should have at least one unit test.
4. Run `dotnet test` locally before pushing.
5. Open a PR against `main`. The `PR` workflow must pass before merge.

## Commit messages

Free-form is fine; descriptive titles preferred. If a commit closes an issue, reference it (`Closes #42`).

## Code style

- File-scoped namespaces.
- `using` directives outside the namespace.
- Private instance fields: `_camelCase`.
- Private `const` and `static readonly` fields: `PascalCase`.
- Prefer `init`-only properties on DTOs; mutable state belongs in ViewModels or domain services.
- ViewModels must not reference WPF types (`Visibility`, `Brush`, `Window`, `Application.Current`). Use bindings + converters in XAML if a translation is needed.
- Code-behind for windows is constructor + `DataContext` assignment + minimal lifecycle plumbing. Service calls and state belong in the ViewModel.
- Logs use `ILogger<T>` with structured templates (`logger.LogInformation("X happened for {TenantId}", id)`) — never string interpolation in the message argument.

The `.editorconfig` enforces most of this. Run `dotnet format --verify-no-changes` to check.

## Architecture conventions

- New options classes go in `Configuration/` and bind from `App:<Area>` sections.
- Domain services go in capability-named folders (`Auth/`, `AzureCloud/`, `Logging/`).
- ViewModels in `ViewModels/`.
- Wire-shape DTOs (when added) go in `Dto/`. In-app domain types in `Models/`.
- Paths go through `IAppPaths`. Do **not** compute paths from `Environment.ProcessPath` or `Assembly.Location` — those point inside the Velopack-managed install dir and are wiped on update.
- HTTP clients are obtained from `IHttpClientFactory` by name (`HttpClientNames.Graph` / `Arm`). Don't `new HttpClient()`.
- Token credentials are obtained from `ICredentialFactory.GetForTenant(tenantId)`. Don't construct `InteractiveBrowserCredential` directly.

## Tests

- xUnit + NSubstitute for mocks.
- Test method names may contain underscores (`Method_State_Outcome` is fine — CA1707 is suppressed in the test project).
- Avoid hitting the network from tests. Use a `DelegatingHandler` test double or stub the HTTP client.
- ViewModel tests should construct the VM directly and exercise commands via `ICommand.ExecuteAsync` / `CanExecute`.

## Branch protection

`main` requires:

- The `build-and-test` status check from `.github/workflows/pr.yml`
- All conversations resolved before merge

These rules are configured in the GitHub UI; this document records the intent so reviewers know what to expect.

## Releasing

Releases are tag-driven. To cut one:

1. Update `CHANGELOG.md`: move entries from `[Unreleased]` to a new `[X.Y.Z]` section with today's date.
2. Commit on `main` and push.
3. Tag the commit: `git tag -a vX.Y.Z -m "vX.Y.Z" && git push origin vX.Y.Z`.

The `.github/workflows/release.yml` workflow runs on tag push. It:

- Builds and runs the full test suite. A test failure blocks the release.
- Publishes a self-contained `win-x64` build via `dotnet publish`.
- Packages with Velopack (`vpk pack`) producing the installer and update artifacts in `Releases/`.
- Generates a CycloneDX SBOM (`sbom.json`).
- Attests the build provenance for the produced exe and nupkg via `actions/attest-build-provenance`.
- Creates a **draft** GitHub Release with all artifacts attached.

Review the draft release on GitHub, then publish it manually. The "draft" gate is deliberate — published Velopack releases auto-roll out to installed users, so a manual review prevents accidental bad releases.

Code signing is not enabled for v0.x. Release integrity is established via GitHub's build-provenance attestation (see [SECURITY.md](SECURITY.md)).

Releases are also triggerable manually from the GitHub Actions tab via `workflow_dispatch` (supply a version like `1.2.3`). The workflow creates the matching tag for you when the release is published.

The release workflow also packs `AzureTray.Plugin.Contracts`, `AzureTray.Plugin.PIM`, and `AzureTray.Plugin.LAPS` as NuGet packages and (when the `NUGET_API_KEY` repo secret is present) pushes them to nuget.org. For test-publishing prereleases from a developer machine without going through CI, use [scripts/publish-plugins-prerelease.ps1](scripts/publish-plugins-prerelease.ps1).

## Building a plugin for AzureTray

A plugin is a separate assembly that implements `ITrayPlugin` from the `AzureTray.Plugin.Contracts` NuGet package. The host discovers plugins two ways:

1. **Local plugins folder** — `%LOCALAPPDATA%\AzureTray.Data\plugins\` is scanned at startup. Drop a `.nupkg` in via **Settings → Install from file...** and the host extracts and loads it.
2. **NuGet** — **Settings → Browse online plugins** queries nuget.org for packages tagged `proxylayer.azuretray-plugin` and lists them inline. The discovery tag is the gate: nothing without it shows up in the browser.

## Publishing a plugin to NuGet

To make your plugin discoverable through the in-app browser:

1. Set `<IsPackable>true</IsPackable>` and add `proxylayer.azuretray-plugin` to `<PackageTags>` (see the [Minimum csproj](#minimum-csproj) below).
2. Run `dotnet pack -c Release` to produce a `.nupkg`.
3. `dotnet nuget push` your package to nuget.org. Within ~30 seconds of indexing it'll appear in **Browse online plugins** for any user with the matching host version.

There is no curated registry to apply to — publish to NuGet with the tag and you're listed. What the host does on the user's behalf at install time:

| Check | When |
|---|---|
| **Tag match** | NuGet search is filtered by `proxylayer.azuretray-plugin`; the host re-verifies the tag is in the returned package metadata before installing. |
| **GHSA advisory lookup** | Shallow query against `(packageId, version)` via the GitHub Advisory Database. High/Critical findings prompt the user with a Yes/No confirm; lower severities log only. |
| **Authenticode signature** | If the binary isn't signed, the host prompts the user once with the source URL + the list of completed checks. Decline = nothing on disk. Accept = installed. `RequireTrustedPublisher` mode in `appsettings.json` skips the prompt and pins to specific thumbprints (org-managed deployments). |

There is **no** mandatory source-repo verification or VirusTotal scan today — those were CI-side gates of the previous curated registry. With NuGet-tag discovery, the host trusts NuGet's existing publish-time moderation; user consent fills the rest of the gap.

### Minimum csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>

    <IsPackable>true</IsPackable>
    <PackageId>YourOrg.AzureTray.Plugin.YourPlugin</PackageId>
    <PackageDescription>What your plugin does.</PackageDescription>
    <!-- proxylayer.azuretray-plugin is the discovery tag the host
         queries on nuget.org. Without it your package won't show
         up in the in-app browser. -->
    <PackageTags>proxylayer.azuretray-plugin;your;extra;tags</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <!-- PrivateAssets="all" is mandatory: the host loads its OWN
         copy of AzureTray.Plugin.Contracts; shipping a transitive
         dependency on it would cause duplicate type loads and
         break the ITrayPlugin cast. -->
    <PackageReference Include="AzureTray.Plugin.Contracts" Version="0.2.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

### Plugins that depend on extra NuGet packages

If your plugin needs a package the host doesn't ship (e.g. `Newtonsoft.Json`), bundle the dependency DLLs inside your own .nupkg's `lib/<tfm>/` folder. The host extracts every DLL from there into the plugin's install folder and resolves them at load time.

The recipe is two parts. First, copy transitive deps into your build output:

```xml
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

Second, tell `dotnet pack` to include them in the .nupkg's `lib/<tfm>/`:

```xml
<PropertyGroup>
  <TargetsForTfmSpecificBuildOutput>$(TargetsForTfmSpecificBuildOutput);PackTransitiveAssets</TargetsForTfmSpecificBuildOutput>
</PropertyGroup>

<Target Name="PackTransitiveAssets">
  <ItemGroup>
    <!-- RuntimeCopyLocalItems lists NuGet-resolved runtime DLLs.
         Adding them as BuildOutputInPackage puts each in lib/<tfm>/
         of the resulting .nupkg. -->
    <BuildOutputInPackage Include="@(RuntimeCopyLocalItems)" />
  </ItemGroup>
</Target>
```

Don't worry about `AzureTray.Plugin.Contracts` showing up there — `PrivateAssets="all"` on its reference keeps it out of `RuntimeCopyLocalItems`.

### Required permissions

Plugins declare their delegated Graph/ARM scopes via `ITrayPlugin.RequiredPermissions`. After install, the host surfaces those scopes to the user and prompts them to run **Settings → Fix permissions** on each tenant where the plugin should operate. See `AzureTray.Plugin.PIM/Permissions/PimRequiredPermissions.cs` for the in-repo example.
