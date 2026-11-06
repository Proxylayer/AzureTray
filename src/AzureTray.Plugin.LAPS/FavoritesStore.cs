using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.LAPS;

// User-starred menu rows, persisted as a flat key set in the plugin's data
// directory. Keys are the rows' stable menu Keys, so a favorite survives
// display-text changes and re-sorts across restarts.
internal sealed class FavoritesStore
{
    private readonly IPluginContext _context;
    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public FavoritesStore(IPluginContext context)
    {
        _context = context;
        Load();
    }

    public bool Contains(string key)
    {
        lock (_gate) return _keys.Contains(key);
    }

    public void Toggle(string key)
    {
        lock (_gate)
        {
            if (!_keys.Add(key)) _keys.Remove(key);
        }
        Save();
    }

    private string StorePath => Path.Combine(_context.DataDir, "favorites.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            using var stream = File.OpenRead(StorePath);
            var entries = JsonSerializer.Deserialize<string[]>(stream);
            if (entries is null) return;

            lock (_gate)
            {
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry)) _keys.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            // A truncated or hand-edited file reads as "no favorites yet";
            // the next toggle rewrites it whole.
            _context.Logger.LogWarning(ex, "Favorites load failed; starting empty.");
        }
    }

    private void Save()
    {
        string[] snapshot;
        lock (_gate) snapshot = _keys.ToArray();

        try
        {
            Directory.CreateDirectory(_context.DataDir);
            using var stream = File.Create(StorePath);
            JsonSerializer.Serialize(stream, snapshot);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "Favorites save failed; favorites are in-memory only this session.");
        }
    }
}
