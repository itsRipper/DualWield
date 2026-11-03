using System;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.Settings;
using EFT.Settings.Control;
using EFT.UI.Screens;
using EFT.UI;
using BepInEx.Configuration;
using UnityEngine;

namespace DualWieldLab;

// Native command delivery is authoritative. Cached keys only release the latch;
// Update never synthesizes mouse fire or changes the game's bindings.
internal sealed class PairedInput
{
    private readonly KeyCode[][] _aimChords;
    private readonly KeyCode[][] _shootChords;
    internal PlayerOwner Owner { get; }
    internal FirearmHandsInputTranslator Translator { get; }
    internal string BindingSummary { get; }

    private PairedInput(PlayerOwner owner, KeyCode[][] aim, KeyCode[][] shoot, string summary)
    {
        Owner = owner;
        _aimChords = aim;
        _shootChords = shoot;
        Translator = owner.HandsInputTranslator as FirearmHandsInputTranslator
            ?? throw new InvalidOperationException("Native firearm input translator unavailable.");
        BindingSummary = summary;
    }

    internal static PairedInput Create(Player player)
    {
        PlayerOwner owner = player.GetComponent<PlayerOwner>();
        if (owner == null) throw new InvalidOperationException("Native player input owner unavailable.");
        var controls = Singleton<SettingsManager>.Instance.Control.Settings;
        var aim = ControlSettingsGroup.GetKeyGroup(controls.UserKeyBindings.Value, EGameKey.Aim);
        var shoot = ControlSettingsGroup.GetKeyGroup(controls.UserKeyBindings.Value, EGameKey.Shoot);
        if (aim == null || shoot == null) throw new InvalidOperationException("Native Shoot/Aim bindings unavailable.");
        if (aim.pressType != EPressType.Press && aim.pressType != EPressType.Continuous)
            throw new InvalidOperationException("Offhand requires native Aim binding type Press or Continuous; release/double/long bindings are unsupported.");
        if (shoot.pressType != EPressType.Press && shoot.pressType != EPressType.Continuous)
            throw new InvalidOperationException("Primary requires native Shoot binding type Press or Continuous while paired.");
        KeyCode[][] aimChords = Chords(aim), shootChords = Chords(shoot);
        if (aimChords.Length == 0 || shootChords.Length == 0)
            throw new InvalidOperationException("Shoot/Aim must have a keyboard or mouse binding.");
        if (aimChords.Any(a => shootChords.Any(s => a.All(s.Contains) || s.All(a.Contains))))
            throw new InvalidOperationException("Native Shoot/Aim bindings overlap; configure separate triggers before pairing.");
        return new PairedInput(owner, aimChords, shootChords,
            "primary=" + Describe(shootChords) + "; offhand=" + Describe(aimChords) + "; aim_press_type=" + aim.pressType);
    }

    private static KeyCode[][] Chords(KeyGroup group) => group.variants
        .Where(v => !v.isAxis && v.keyCode != null && v.keyCode.Count > 0 && !v.keyCode.Contains(KeyCode.None))
        .Select(v => v.keyCode.ToArray()).ToArray();

    private static string Describe(KeyCode[][] chords) => string.Join("/", chords.Select(c => string.Join("+", c.Select(k => k.ToString()))));

    internal bool IsAimHeld()
        => IsHeld(_aimChords);

    internal bool IsShootHeld() => IsHeld(_shootChords);

    internal void ValidateShortcuts(params KeyboardShortcut[] shortcuts)
    {
        for (int i = 0; i < shortcuts.Length; i++)
        {
            var shortcut = shortcuts[i];
            if (shortcut.MainKey == KeyCode.None) continue;
            if (_aimChords.Any(c => c.Contains(shortcut.MainKey)) || _shootChords.Any(c => c.Contains(shortcut.MainKey)))
                throw new InvalidOperationException("Dual shortcut overlaps a native fire/aim key; choose a separate chord.");
            var keys = shortcut.Modifiers.Concat(new[] { shortcut.MainKey }).ToArray();
            for (int j = 0; j < i; j++)
            {
                if (shortcuts[j].MainKey == KeyCode.None) continue;
                var other = shortcuts[j].Modifiers.Concat(new[] { shortcuts[j].MainKey }).ToArray();
                if (keys.Length == other.Length && keys.All(other.Contains))
                    throw new InvalidOperationException("Dual pair/reload/mode shortcuts must be distinct.");
            }
        }
    }

    private static bool IsHeld(KeyCode[][] chords)
    {
        foreach (KeyCode[] chord in chords)
        {
            bool held = true;
            foreach (KeyCode key in chord) held &= Input.GetKey(key);
            if (held) return true;
        }
        return false;
    }

    internal static bool AllowsGameplay(PlayerOwner? owner)
    {
        if (owner?.Player is HideoutPlayer && HideoutRangeContext.Refusal(owner.Player, owner) != null) return false;
        if (owner == null || !owner.isActiveAndEnabled || owner.Player == null || !owner.Player.IsYourPlayer
            || owner.State != PlayerOwner.EState.Started || owner.InputTree == null || !owner.InputTree.Contains(owner)
            || !Application.isFocused || Cursor.lockState != CursorLockMode.Locked || Time.timeScale <= 0f
            || owner.Player.IsInventoryOpened || !owner.Player.HealthController.IsAlive
            || !EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI)) return false;
        var ui = ItemUiContext.Instance;
        if (ui != null && (ui.MessageWindowActive || ui.ScrolledMessageWindowActive)) return false;
        return owner is GamePlayerOwner game && game.CanTranslateCommand(ECommand.ToggleAlternativeShooting);
    }
}
