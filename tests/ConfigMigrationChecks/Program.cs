using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using DualWieldLab;
using UnityEngine;

// Migration of the renamed plugins' settings, run against BepInEx's own ConfigFile in a temporary
// directory. No game, profile or real configuration file is touched.
internal static class Program
{
    private const string RuntimeFile = "itsripper.dualwield.cfg";
    private const string HolsterFile = "itsripper.dualwield.dualholster.cfg";
    private const string HolsterLegacy = "local.dualwieldlab.dualholster.cfg";
    private static int _checks, _failures;

    // The settings file of a pre-release installation, with custom shortcuts and keys of older versions.
    private static readonly string[] LegacyRuntime =
    {
        "## Settings file was created by plugin Dual Wield Lab v0.22.0", "",
        "[Development]", "Enabled = false", "ExperimentalLeftHandIK = true", "",
        "[Diagnostics]", "Enabled = true", "",
        "[Input]", "PairShortcut = V + LeftAlt", "UseNativeAimBindingForOffhand = true", "EnableDiagnosticKeys = false",
        "TogglePair = F8", "ReloadOffhand = F11", "ChangeOffhandMode = F5", "LeftHandModifier = Mouse3",
        "ExamineOffhand = L + LeftAlt", "",
        "[Pair]", "OffhandItemId = 0123456789abcdef01234567", "AutoPairFromHolsters = false", "",
        "[Pose]", "OffhandOffset = {\"x\":-0.3,\"y\":0.0,\"z\":0.0}", "UseAimingReadyStance = false",
    };

    private static void Check(string name, bool passed)
    {
        _checks++;
        if (!passed) _failures++;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "dualwield-config-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Runtime(root);
            DualHolster(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"{_checks - _failures}/{_checks} configuration migration checks passed.");
        return _failures == 0 ? 0 : 1;
    }

    private static string NewDirectory(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    // Mirrors RuntimePlugin.Awake: existence check, Bind, then migration.
    private static (DualWieldSettings Settings, ConfigMigration.Outcome Outcome, List<string> Warnings) StartRuntime(string directory)
    {
        var config = new ConfigFile(Path.Combine(directory, RuntimeFile), false);
        bool existed = File.Exists(config.ConfigFilePath);
        var settings = new DualWieldSettings(config);
        var warnings = new List<string>();
        var outcome = ConfigMigration.Apply(config, existed, DualWieldSettings.LegacyConfigFile, DualWieldSettings.RenamedKeys, _ => { }, warnings.Add);
        return (settings, outcome, warnings);
    }

    private static void Runtime(string root)
    {
        string dir = NewDirectory(root, "runtime");
        string legacy = Path.Combine(dir, DualWieldSettings.LegacyConfigFile);
        File.WriteAllLines(legacy, LegacyRuntime);
        var (s, outcome, warnings) = StartRuntime(dir);
        Check("Runtime: a legacy configuration is migrated", outcome == ConfigMigration.Outcome.Migrated && warnings.Count == 0);
        Check("Runtime: Development/Enabled moves to General/Enabled", !s.Enabled.Value);
        Check("Runtime: custom shortcuts survive", s.ReloadOffhand.Value.MainKey == KeyCode.F11 && !s.ReloadOffhand.Value.Modifiers.Any()
            && s.ModeOffhand.Value.MainKey == KeyCode.F5 && s.LeftModifier.Value == KeyCode.Mouse3
            && s.PairShortcut.Value.MainKey == KeyCode.V && s.PairShortcut.Value.Modifiers.SequenceEqual(new[] { KeyCode.LeftAlt }));
        Check("Runtime: pairing and stance settings survive, diagnostics start off", !s.Diagnostics.Value && !s.AutoPair.Value && !s.ReadyAim.Value
            && s.OffhandId.Value == "0123456789abcdef01234567");
        string written = File.ReadAllText(Path.Combine(dir, RuntimeFile));
        Check("Runtime: the new file holds the migrated values and no removed setting",
            written.Contains("[General]") && written.Contains("ReloadOffhand = F11") && !written.Contains("[Development]")
            && !written.Contains("ExperimentalLeftHandIK") && !written.Contains("TogglePair") && !written.Contains("OffhandOffset"));
        Check("Runtime: the legacy file is kept as a .migrated backup", !File.Exists(legacy) && File.Exists(legacy + ConfigMigration.BackupSuffix));

        var again = StartRuntime(dir);
        Check("Runtime: a repeated start does not migrate again and keeps the values", again.Outcome == ConfigMigration.Outcome.NotNeeded
            && again.Settings.ReloadOffhand.Value.MainKey == KeyCode.F11 && !again.Settings.Enabled.Value);

        string fresh = NewDirectory(root, "fresh");
        var clean = StartRuntime(fresh);
        Check("Runtime: a fresh installation uses the defaults", clean.Outcome == ConfigMigration.Outcome.NoLegacyFile && clean.Settings.Enabled.Value
            && clean.Settings.ReloadOffhand.Value.MainKey == KeyCode.R && clean.Settings.ReloadOffhand.Value.Modifiers.SequenceEqual(new[] { KeyCode.LeftAlt })
            && clean.Settings.LeftModifier.Value == KeyCode.Mouse4 && !clean.Settings.Diagnostics.Value && clean.Warnings.Count == 0);

        string both = NewDirectory(root, "both");
        File.WriteAllLines(Path.Combine(both, RuntimeFile), new[] { "[Input]", "ReloadOffhand = F2" });
        File.WriteAllLines(Path.Combine(both, DualWieldSettings.LegacyConfigFile), LegacyRuntime);
        var existing = StartRuntime(both);
        Check("Runtime: an existing new configuration wins over the legacy file", existing.Outcome == ConfigMigration.Outcome.NotNeeded
            && existing.Settings.ReloadOffhand.Value.MainKey == KeyCode.F2 && existing.Settings.Enabled.Value
            && File.Exists(Path.Combine(both, DualWieldSettings.LegacyConfigFile)));

        string partial = NewDirectory(root, "partial");
        File.WriteAllLines(Path.Combine(partial, DualWieldSettings.LegacyConfigFile),
            new[] { "[Input]", "ReloadOffhand = F11", "this line is damaged", "ChangeOffhandMode = NotAKey", "[Pose", "UseAimingReadyStance = false" });
        var damaged = StartRuntime(partial);
        Check("Runtime: readable settings of a damaged file migrate, the damaged lines are reported", damaged.Outcome == ConfigMigration.Outcome.Migrated
            && damaged.Settings.ReloadOffhand.Value.MainKey == KeyCode.F11 && damaged.Warnings.Any(w => w.Contains("line(s) 3, 5")));
        Check("Runtime: an invalid value keeps its default and is reported", damaged.Settings.ModeOffhand.Value.MainKey == KeyCode.B
            && damaged.Settings.ModeOffhand.Value.Modifiers.SequenceEqual(new[] { KeyCode.LeftAlt }) && damaged.Warnings.Any(w => w.Contains("Input/ChangeOffhandMode")));

        string garbage = NewDirectory(root, "garbage");
        File.WriteAllText(Path.Combine(garbage, DualWieldSettings.LegacyConfigFile), "\u0000\u0001binary\u0002 garbage without settings");
        var unreadable = StartRuntime(garbage);
        Check("Runtime: a file without settings is reported, left unchanged and the defaults are used",
            unreadable.Outcome == ConfigMigration.Outcome.LegacyUnreadable && unreadable.Warnings.Count == 1
            && File.Exists(Path.Combine(garbage, DualWieldSettings.LegacyConfigFile)) && unreadable.Settings.Enabled.Value);
    }

    private static (ConfigEntry<string> Left, ConfigEntry<bool> Preview, ConfigMigration.Outcome Outcome) StartDualHolster(string directory)
    {
        var config = new ConfigFile(Path.Combine(directory, HolsterFile), false);
        bool existed = File.Exists(config.ConfigFilePath);
        // Same entries as DualHolsterPlugin.Awake binds before it migrates.
        var left = config.Bind("Layout", "LeftLabel", "LEFT HOLSTER", "");
        config.Bind("Layout", "RightLabel", "RIGHT HOLSTER", "");
        var preview = config.Bind("Presentation", "DualPistolCharacterPreview", true, "");
        var outcome = ConfigMigration.Apply(config, existed, HolsterLegacy, new Dictionary<string, string>(), _ => { }, _ => { });
        return (left, preview, outcome);
    }

    private static void DualHolster(string root)
    {
        string dir = NewDirectory(root, "holster");
        File.WriteAllLines(Path.Combine(dir, HolsterLegacy),
            new[] { "[Layout]", "LeftLabel = OFF HAND", "RightLabel = RIGHT HOLSTER", "[Presentation]", "DualPistolCharacterPreview = false", "MenuDualPistolPreview = true" });
        var migrated = StartDualHolster(dir);
        Check("Dual holster: a legacy configuration is migrated with its custom values", migrated.Outcome == ConfigMigration.Outcome.Migrated
            && migrated.Left.Value == "OFF HAND" && !migrated.Preview.Value);
        Check("Dual holster: a key the plugin no longer reads is not carried over",
            !File.ReadAllText(Path.Combine(dir, HolsterFile)).Contains("MenuDualPistolPreview"));
        var again = StartDualHolster(dir);
        Check("Dual holster: a repeated start keeps the migrated values", again.Outcome == ConfigMigration.Outcome.NotNeeded && again.Left.Value == "OFF HAND");
        var fresh = StartDualHolster(NewDirectory(root, "holster-fresh"));
        Check("Dual holster: a fresh installation uses the defaults", fresh.Outcome == ConfigMigration.Outcome.NoLegacyFile
            && fresh.Left.Value == "LEFT HOLSTER" && fresh.Preview.Value);
    }
}
