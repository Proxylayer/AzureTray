using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace AzureTray.Plugins;

// Per-plugin AssemblyLoadContext. Collectible so plugins can be unloaded.
//
// Resolution order on a plugin-requested assembly:
//   1. Defer to the host (default context) for anything the host has
//      already loaded — most importantly AzureTray.Plugin.Contracts and
//      Microsoft.Extensions.*. Without this, ITrayPlugin from the plugin
//      and the host's ITrayPlugin would be DIFFERENT Types and the cast
//      would fail.
//   2. AssemblyDependencyResolver — works when the plugin shipped its
//      .deps.json alongside the DLL (the default for `dotnet publish`
//      output and for nuget packages with .deps.json packed into lib/).
//   3. Sibling-DLL lookup — for plugins that just zipped their bin/
//      output into the .nupkg without a .deps.json, look for
//      "{AssemblyName}.dll" in the plugin's containing folder and load
//      from there. Plugin authors who set
//      <CopyLocalLockFileAssemblies>true</…> in their csproj end up with
//      all their transitive deps in lib/<tfm>/ but typically without a
//      .deps.json — this fallback covers them.
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: $"Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath) ?? string.Empty;
    }

    // Loads an assembly into the given context by reading the file bytes
    // into memory first, then calling LoadFromStream. The crucial property:
    // because we never call LoadFromAssemblyPath, Windows never opens a
    // memory-mapped file handle on the DLL, so the file isn't locked for
    // the lifetime of the context. Uninstalling / overwriting a loaded
    // plugin's DLL becomes a plain file delete — no waiting for ALC
    // unload, no waiting for the GC to clear the mmap.
    //
    // If a side-by-side .pdb exists we ship it through too so debugging
    // and exception stack traces stay accurate.
    internal static Assembly LoadFromBytes(AssemblyLoadContext context, string assemblyPath)
    {
        var assemblyBytes = File.ReadAllBytes(assemblyPath);
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            var pdbBytes = File.ReadAllBytes(pdbPath);
            using var asmStream = new MemoryStream(assemblyBytes, writable: false);
            using var pdbStream = new MemoryStream(pdbBytes, writable: false);
            return context.LoadFromStream(asmStream, pdbStream);
        }
        else
        {
            using var asmStream = new MemoryStream(assemblyBytes, writable: false);
            return context.LoadFromStream(asmStream);
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Step 1: defer to the default context if the host already has it.
        foreach (var loaded in Default.Assemblies)
        {
            var loadedName = loaded.GetName();
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, loadedName))
            {
                return null;
            }
        }

        // Step 2: AssemblyDependencyResolver via the .deps.json if present.
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
        {
            return LoadFromBytes(this, resolved);
        }

        // Step 3: sibling-DLL fallback. Looks for {name}.dll next to the
        // plugin DLL, which is where <CopyLocalLockFileAssemblies>
        // dumps every transitive dep when `dotnet pack` runs without
        // generating a side-by-side .deps.json.
        if (!string.IsNullOrEmpty(_pluginDirectory) && !string.IsNullOrEmpty(assemblyName.Name))
        {
            var sibling = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(sibling))
            {
                return LoadFromBytes(this, sibling);
            }
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is not null) return LoadUnmanagedDllFromPath(path);

        // Same sibling fallback for native DLLs the plugin might ship.
        if (!string.IsNullOrEmpty(_pluginDirectory) && !string.IsNullOrEmpty(unmanagedDllName))
        {
            var sibling = Path.Combine(_pluginDirectory, unmanagedDllName);
            if (File.Exists(sibling)) return LoadUnmanagedDllFromPath(sibling);
            sibling = Path.Combine(_pluginDirectory, unmanagedDllName + ".dll");
            if (File.Exists(sibling)) return LoadUnmanagedDllFromPath(sibling);
        }

        return IntPtr.Zero;
    }
}
