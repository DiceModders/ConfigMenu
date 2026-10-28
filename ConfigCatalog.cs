using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace ConfigMenu;

internal sealed class PluginConfig {
    public PluginConfig(string guid, string name, string version, ConfigFile file) {
        Guid = guid;
        Name = name;
        Version = version;
        File = file;
        ConfigPath = DisplayPath(file.ConfigFilePath);
    }

    public string Guid { get; }
    public string Name { get; }
    public string Version { get; }
    public ConfigFile File { get; }
    public string ConfigPath { get; }
    public List<ConfigSection> Sections { get; } = new();
    public int EntryCount { get; private set; }

    public void BuildSections() {
        Sections.Clear();
        EntryCount = 0;

        IEnumerable<ConfigEntryBase> entries;
        try { entries = File.Values; }
        catch (Exception) { entries = Array.Empty<ConfigEntryBase>(); }

        foreach (var group in entries
                     .GroupBy(entry => entry.Definition.Section ?? string.Empty)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)) {
            var section = new ConfigSection(string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key);
            section.Entries.AddRange(group.OrderBy(entry => entry.Definition.Key, StringComparer.OrdinalIgnoreCase));
            if (section.Entries.Count == 0) continue;

            Sections.Add(section);
            EntryCount += section.Entries.Count;
        }
    }

    private static string DisplayPath(string? fullPath) {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;

        try {
            string root = Path.GetFullPath(Paths.ConfigPath);
            string path = Path.GetFullPath(fullPath);
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                string relative = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return "BepInEx/config/" + relative.Replace('\\', '/');
            }
        }
        catch (Exception) {
            // The full path is still useful when the relative form cannot be built.
        }

        return fullPath.Replace('\\', '/');
    }
}

internal sealed class ConfigSection {
    public ConfigSection(string name) => Name = name;

    public string Name { get; }
    public List<ConfigEntryBase> Entries { get; } = new();
}

internal static class ConfigCatalog {
    /// <summary>
    /// Takes a snapshot of every loaded plugin that owns a non-empty BepInEx configuration file. The snapshot is
    /// rebuilt on demand because several mods bind their settings lazily during a match.
    /// </summary>
    public static List<PluginConfig> Scan(ManualLogSource? log) {
        var result = new List<PluginConfig>();

        try {
            var chainloader = IL2CPPChainloader.Instance;
            if (chainloader == null) {
                log?.LogWarning("ConfigMenu: the BepInEx chainloader is not available yet.");
                return result;
            }

            foreach (var pair in chainloader.Plugins) {
                try {
                    var info = pair.Value;
                    if (info?.Instance is not BasePlugin plugin) continue;

                    var file = plugin.Config;
                    if (file == null || file.Count == 0) continue;

                    string guid = string.IsNullOrWhiteSpace(info.Metadata?.GUID) ? pair.Key : info.Metadata!.GUID;
                    string name = string.IsNullOrWhiteSpace(info.Metadata?.Name) ? guid : info.Metadata!.Name;
                    string version = info.Metadata?.Version?.ToString() ?? string.Empty;

                    var model = new PluginConfig(guid, name, version, file);
                    model.BuildSections();
                    if (model.EntryCount == 0) continue;

                    result.Add(model);
                }
                catch (Exception e) {
                    log?.LogWarning($"ConfigMenu: could not read the configuration of '{pair.Key}': {e.Message}");
                }
            }
        }
        catch (Exception e) {
            log?.LogError($"ConfigMenu: configuration scan failed: {e}");
        }

        return result
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plugin => plugin.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
