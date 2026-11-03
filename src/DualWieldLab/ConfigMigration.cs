using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace DualWieldLab;

// One-time copy of a renamed plugin's settings from its previous BepInEx configuration file.
// BepInEx creates a plugin's file on its first Bind, so the caller records whether the file
// existed before binding: an existing file is never overwritten with legacy values. The legacy
// file is renamed with a ".migrated" suffix afterwards and kept as a backup.
internal static class ConfigMigration
{
    internal const string BackupSuffix = ".migrated";

    internal enum Outcome { NotNeeded, NoLegacyFile, Migrated, LegacyUnreadable }

    internal readonly struct Entry
    {
        internal Entry(string section, string key, string value) { Section = section; Key = key; Value = value; }
        internal string Section { get; }
        internal string Key { get; }
        internal string Value { get; }
    }

    // Same line rules as ConfigFile.Reload; lines it would skip silently are reported instead.
    internal static List<Entry> Parse(IEnumerable<string> lines, out List<int> unreadableLines)
    {
        var entries = new List<Entry>();
        unreadableLines = new List<int>();
        string section = string.Empty;
        int number = 0;
        foreach (string raw in lines)
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
            string[] parts = line.Split(new[] { '=' }, 2);
            if (parts.Length == 2 && section.Length > 0 && parts[0].Trim().Length > 0) entries.Add(new Entry(section, parts[0].Trim(), parts[1].Trim()));
            else unreadableLines.Add(number);
        }
        return entries;
    }

    private static string Normalize(string value) => value.Replace(" ", string.Empty).ToLowerInvariant();

    // renamed: "OldSection/OldKey" -> "NewSection/NewKey", or -> "" to leave a setting at its default.
    // Legacy settings the plugin no longer binds are left out; missing ones keep their defaults.
    internal static Outcome Apply(ConfigFile config, bool fileExistedBeforeBind, string legacyFileName,
        IReadOnlyDictionary<string, string> renamed, Action<string> info, Action<string> warn)
    {
        if (fileExistedBeforeBind) return Outcome.NotNeeded;
        string legacy = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath) ?? string.Empty, legacyFileName);
        if (!File.Exists(legacy)) return Outcome.NoLegacyFile;

        List<Entry> entries;
        List<int> unreadable;
        try { entries = Parse(File.ReadAllLines(legacy), out unreadable); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            warn($"Settings from {legacyFileName} could not be read ({e.Message}); defaults are used and the file is left unchanged.");
            return Outcome.LegacyUnreadable;
        }
        if (entries.Count == 0)
        {
            warn($"{legacyFileName} contains no readable settings; defaults are used and the file is left unchanged.");
            return Outcome.LegacyUnreadable;
        }
        if (unreadable.Count > 0)
            warn($"{legacyFileName}: line(s) {string.Join(", ", unreadable)} are not settings and were skipped.");

        bool save = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        int copied = 0;
        try
        {
            foreach (Entry entry in entries)
            {
                string name = entry.Section + "/" + entry.Key;
                if (renamed.TryGetValue(name, out string? target))
                {
                    if (target.Length == 0) continue;
                    name = target;
                }
                int slash = name.IndexOf('/');
                var definition = new ConfigDefinition(name.Substring(0, slash), name.Substring(slash + 1));
                if (!config.ContainsKey(definition)) continue;
                ConfigEntryBase setting = config[definition];
                setting.SetSerializedValue(entry.Value);
                // Some converters (KeyboardShortcut) turn an invalid value into an empty one instead
                // of failing: a value that does not read back is replaced by the default.
                if (Normalize(setting.GetSerializedValue()) != Normalize(entry.Value))
                {
                    setting.BoxedValue = setting.DefaultValue;
                    warn($"{legacyFileName}: \"{entry.Value}\" is not a valid value for {definition.Section}/{definition.Key}; the default is used.");
                    continue;
                }
                copied++;
            }
        }
        finally { config.SaveOnConfigSet = save; }
        config.Save();

        string backup = legacy + BackupSuffix;
        if (!File.Exists(backup)) File.Move(legacy, backup);
        info($"Migrated {copied} setting(s) from {legacyFileName} to {Path.GetFileName(config.ConfigFilePath)}; the old file is kept as {Path.GetFileName(File.Exists(backup) ? backup : legacy)}.");
        return Outcome.Migrated;
    }
}
