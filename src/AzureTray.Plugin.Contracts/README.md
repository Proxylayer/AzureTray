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
    <PackageReference Include="AzureTray.Plugin.Contracts" Version="0.2.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

## Version gate

Plugins declare `ITrayPlugin.ApiVersion` and the host rejects any plugin whose value doesn't equal `PluginApiVersion.Current`. The contract version bumps only on breaking changes — minor host releases keep loading existing plugins.

## More

See [CONTRIBUTING.md](https://github.com/Proxylayer/AzureTray/blob/main/CONTRIBUTING.md) in the host repo for the full plugin author guide, including how to submit a plugin to the curated registry the in-app browser pulls from.
