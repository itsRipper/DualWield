using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace DualWieldLab;

internal sealed class DualWieldSettings
{
    // The runtime's configuration file before the plugin was renamed, and keys that moved.
    internal const string LegacyConfigFile = "local.dualwieldlab.glock17.cfg";
    // An empty target is not carried over: diagnostics are for troubleshooting and start off.
    internal static readonly IReadOnlyDictionary<string, string> RenamedKeys = new Dictionary<string, string>
    {
        ["Development/Enabled"] = "General/Enabled",
        ["Diagnostics/Enabled"] = "",
    };

    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<bool> AutoPair { get; }
    internal ConfigEntry<string> OffhandId { get; }
    internal ConfigEntry<KeyboardShortcut> PairShortcut { get; }
    internal ConfigEntry<KeyboardShortcut> ReloadOffhand { get; }
    internal ConfigEntry<KeyboardShortcut> ModeOffhand { get; }
    internal ConfigEntry<KeyCode> LeftModifier { get; }
    internal ConfigEntry<bool> ReadyAim { get; }
    internal ConfigEntry<bool> Diagnostics { get; }

    internal DualWieldSettings(ConfigFile config)
    {
        Enabled = config.Bind("General", "Enabled", true, "Master switch. False disables dual wielding without uninstalling.");
        AutoPair = config.Bind("Pair", "AutoPairFromHolsters", true, "Pair automatically when the pistol in HOLSTER is drawn while HOLSTERLEFT also holds a compatible pistol. Alt+V still unpairs (until the next weapon change) and pairs manually.");
        OffhandId = config.Bind("Pair", "OffhandItemId", "", "Item id of a backpack pistol to pair with when the left holster is empty. Empty: the only compatible backpack pistol, if there is exactly one.");
        PairShortcut = config.Bind("Input", "PairShortcut", new KeyboardShortcut(KeyCode.V, KeyCode.LeftAlt), "Pair/unpair. Choose a chord separate from Shoot/Aim; commands on the pairing frame are consumed except trigger releases.");
        ReloadOffhand = config.Bind("Input", "ReloadOffhand", new KeyboardShortcut(KeyCode.R, KeyCode.LeftAlt), "Reload the left pistol.");
        ModeOffhand = config.Bind("Input", "ChangeOffhandMode", new KeyboardShortcut(KeyCode.B, KeyCode.LeftAlt), "Cycle the left pistol's fire modes. The right pistol's fire mode stays on EFT's own binding.");
        LeftModifier = config.Bind("Input", "LeftHandModifier", KeyCode.Mouse4, "Hold while using an EFT weapon action to send it to the left pistol: reload, fire mode, fire-mode check, magazine check, chamber check / malfunction clear, examine. EFT's own bindings decide the action (by default R, B, Alt+B, Alt+T, Shift+T, L), so rebinding them in EFT carries over. While paired and held, the key's own EFT binding and right-only weapon actions are suppressed. Mouse4 is the side mouse button, Mouse3 the other one. None disables.");
        ReadyAim = config.Bind("Pose", "UseAimingReadyStance", true, "Paired aiming-ready stance. False uses the wider hip stance. Aim never changes the stance while paired; sprinting lowers it.");
        Diagnostics = config.Bind("Diagnostics", "Enabled", false, "Write per-shot, pose and reload diagnostics (DW_* lines) to the BepInEx log. Warnings and errors are always logged.");
    }
}
