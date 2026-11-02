using System;
using System.Collections.Generic;

namespace Dasher.Windows.Services;

/// <summary>
/// Modifier hygiene for direct-mode injection (RFC 0021 injection contract;
/// issue #90, log-confirmed 2026-11-02): a physically-held Ctrl (Heide's
/// TouchMousePointer sticky key, forgotten after an OmniClip shortcut) turned
/// every injected character into a Ctrl+letter shortcut chord that choked the
/// target's host thread for minutes. Pure planner: given the current OS
/// modifier state, produce the release/restore key sequence so each injected
/// SendInput lands as plain input, with the held modifiers restored after —
/// atomically inside the SAME SendInput batch, so nothing else can interleave.
///
/// Shift is deliberately NOT shielded: plain-text injection is not altered by
/// Shift, the chord injectors send their own complete modifier set, and a
/// user mid-chord (Shift+arrow in the target) must not have their Shift
/// stolen. Ctrl/Alt/Win are the corruption class. Both SIDES are queried and
/// released independently (RCtrl/AltGr users hold the right variants).
/// </summary>
public static class InjectionShield
{
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;   // left Alt
    public const ushort VK_RMENU = 0xA5;   // right Alt / AltGr
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;

    /// <summary>The held modifiers that must be released around an injection,
    /// sided: each held side is released and restored individually.</summary>
    public static IReadOnlyList<ushort> HeldModifiers(
        bool lctrl, bool rctrl, bool lalt, bool ralt, bool lwin, bool rwin)
    {
        var held = new List<ushort>(6);
        if (lctrl) held.Add(VK_LCONTROL);
        if (rctrl) held.Add(VK_RCONTROL);
        if (lalt) held.Add(VK_LMENU);
        if (ralt) held.Add(VK_RMENU);
        if (lwin) held.Add(VK_LWIN);
        if (rwin) held.Add(VK_RWIN);
        return held;
    }

    /// <summary>One planned keyboard event (pure — injectors translate to
    /// INPUT structs; tests assert the layout).</summary>
    public readonly record struct KeyEvent(ushort Vk, bool Up);

    /// <summary>
    /// Plan a full shielded injection sequence:
    ///   [held modifiers UP] [own presses] [own presses released, reverse
    ///   order] [held modifiers DOWN again — restore]
    /// All events go into ONE SendInput call (atomic — concurrent input
    /// cannot land between the shield release and the injection).
    /// </summary>
    public static IReadOnlyList<KeyEvent> PlanVkSequence(
        IReadOnlyList<ushort> heldShield, IReadOnlyList<ushort> presses)
    {
        var plan = new List<KeyEvent>(heldShield.Count * 2 + presses.Count * 2);
        foreach (var vk in heldShield) plan.Add(new KeyEvent(vk, Up: true));
        foreach (var vk in presses) plan.Add(new KeyEvent(vk, Up: false));
        for (var i = presses.Count - 1; i >= 0; i--) plan.Add(new KeyEvent(presses[i], Up: true));
        foreach (var vk in heldShield) plan.Add(new KeyEvent(vk, Up: false));
        return plan;
    }
}
