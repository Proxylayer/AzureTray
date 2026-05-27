# AzureTray.Plugin.Contracts

The SDK package for building [AzureTray](https://github.com/Proxylayer/AzureTray) plugins. Reference this package to implement `ITrayPlugin` and ship a plugin the host can load.

## What's inside

- `ITrayPlugin` — the top-level plugin contract.
- `IPluginContext` — services the host hands to the plugin at initialization (logger, HTTP factory, clipboard, notifier, tenant list, badge surface, etc.).
- `PluginMenuItem`, `PluginPermissionRequirement`, `PluginOption`, `PluginTest`, `PluginTenant`, `NotificationRequest`/`NotificationResult` — the supporting data types.
- `PluginApiVersion` — the contract version your plugin must declare it was built against.

## Minimum plugin csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>YourOrg.AzureTray.Plugin.YourPlugin</PackageId>
    <!-- 'proxylayer.azuretray-plugin' is the discovery tag the host queries. -->
    <PackageTags>proxylayer.azuretray-plugin;your;tags</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <!-- PrivateAssets="all" is mandatory. The host loads its OWN copy of
         the contracts assembly into a collectible AssemblyLoadContext;
         shipping a transitive dependency on this package would cause
         duplicate type loads and break the ITrayPlugin cast. -->
    <PackageReference Include="AzureTray.Plugin.Contracts" Version="1.2.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

## Packaging & deployment

### 1. Add the required project property

Add `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` to your `<PropertyGroup>`. This copies every transitive NuGet dependency into the build output so the host can resolve them at runtime without a global package cache.

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  ...
</PropertyGroup>
```

### 2. Publish (recommended) or build

```powershell
# Produces a self-contained output folder with a .deps.json — the preferred path.
dotnet publish YourPlugin.csproj -c Release -o ./out

# A plain build also works; the host falls back to sibling-DLL resolution
# when no .deps.json is present.
dotnet build YourPlugin.csproj -c Release
```

Do **not** ship `AzureTray.Plugin.Contracts.dll` in the output — the `PrivateAssets="all"` reference in your `.csproj` already excludes it. The host loads its own copy of that assembly; a second copy causes a type-identity mismatch and the `ITrayPlugin` cast silently fails.

### 3. Install into the plugins folder

The host scans `%LOCALAPPDATA%\AzureTray.Data\plugins\` at startup using two layouts:

| Layout | When to use | Steps |
|---|---|---|
| **Subfolder (recommended)** | Any plugin with transitive deps | Create `plugins\<YourPackageId>\`, copy your publish output there. The main DLL **must** be named `<folder-name>.dll` (e.g. `plugins\Acme.Plugin.Foo\Acme.Plugin.Foo.dll`). |
| **Flat (legacy)** | Single-DLL plugins with no private deps | Drop `YourPlugin.dll` directly in `plugins\`. |

For the subfolder layout the loader first tries `plugins\<folder>\<folder>.dll`; if that name doesn't exist it scans for any DLL in the folder that contains an `ITrayPlugin` implementation. Framework assemblies whose name starts with `System.`, `Microsoft.`, `Azure.`, or `Newtonsoft.` are skipped during the scan.

### 4. Runtime trust mode

The default trust mode is `AllowUnsigned` (development). For your own testing this means no signing is needed. Deployments configured with `RequireSigned` or `RequireTrustedPublisher` will reject the plugin unless it carries a valid Authenticode signature.

## Version gate

Plugins declare `ITrayPlugin.ApiVersion` (the `PluginApiVersion.Current` value they were built against). The host loads a plugin when that value falls within its **supported range** `[PluginApiVersion.MinSupported, PluginApiVersion.Current]`; anything outside the range is rejected with a logged message naming the range. Use `PluginApiVersion.IsSupported(int)` to test a value.

Because the contracts assembly keeps a **fixed `AssemblyVersion`**, an old plugin always binds to the host's current contracts copy at runtime — the range is the only thing that decides whether that copy will load it.

How the range moves:

- **Additive, binary-compatible changes** (a new default-interface member, a new optional capability interface, a new init-only property on a record) bump `Current` and leave `MinSupported` alone. Plugins built against any version still in the window keep loading — so you can build against an older API and keep running on newer hosts.
- **Breaking changes** raise `MinSupported`, intentionally locking out the now-incompatible older plugins. These should be rare; prefer the additive techniques above.

To run a single plugin binary across a span of hosts, build against the lowest API you need and feature-detect newer host capabilities at runtime via `IPluginContext.HostVersion`.

## More

See [CONTRIBUTING.md](https://github.com/Proxylayer/AzureTray/blob/main/CONTRIBUTING.md) in the host repo for the full plugin author guide, including how to submit a plugin to the curated registry the in-app browser pulls from.
