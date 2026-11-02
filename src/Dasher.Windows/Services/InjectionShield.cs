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
/// stolen. Ctrl/Alt/Win are the corruption class.
/// </summary>
public static class InjectionShield
{
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;    // Alt
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;

    /// <summary>The held modifiers that must be released around an injection.
    /// Logical Ctrl/Alt first (matches how sticky-key tools hold them), then
    /// the per-side Win keys (only held if their side is actually down).</summary>
    public static IReadOnlyList<ushort> HeldModifiers(bool ctrlDown, bool altDown, bool lwinDown, bool rwinDown)
    {
        var held = new List<ushort>(4);
        if (ctrlDown) held.Add(VK_CONTROL);
        if (altDown) held.Add(VK_MENU);
        if (lwinDown) held.Add(VK_LWIN);
        if (rwinDown) held.Add(VK_RWIN);
        return held;
    }
}
