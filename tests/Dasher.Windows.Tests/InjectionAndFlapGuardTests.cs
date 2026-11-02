using Dasher.Windows.Services;
using System.Linq;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// #90 modifier-hygiene planner and #91 read-trust gate — the two
/// log-confirmed guards from Heide's v0.1.38 field logs.
/// </summary>
public class InjectionAndFlapGuardTests
{
    // ── InjectionShield.HeldModifiers (#90) ───────────────────────────────

    [Fact]
    public void Shield_lists_sided_held_modifiers_in_canonical_order()
    {
        var held = InjectionShield.HeldModifiers(
            lctrl: true, rctrl: false, lalt: true, ralt: true, lwin: false, rwin: true);
        Assert.Equal(new ushort[]
        {
            InjectionShield.VK_LCONTROL, InjectionShield.VK_LMENU,
            InjectionShield.VK_RMENU, InjectionShield.VK_RWIN,
        }, held);
    }

    [Fact]
    public void Shield_empty_when_no_modifiers_held()
    {
        Assert.Empty(InjectionShield.HeldModifiers(false, false, false, false, false, false));
    }

    [Fact]
    public void Shield_covers_alone_left_ctrl_the_reported_field_case()
    {
        // Heide's case: only Ctrl held (TouchMousePointer sticky after an
        // OmniClip shortcut) — the shield must release exactly that side
        var held = InjectionShield.HeldModifiers(true, false, false, false, false, false);
        Assert.Single(held);
        Assert.Equal(InjectionShield.VK_LCONTROL, held[0]);
    }

    // ── InjectionShield.PlanVkSequence (#90 — the review-loop-1 inversion) ──

    [Fact]
    public void Plan_held_ctrl_plus_bare_arrow_is_shielded_char_move()
    {
        // The review-loop-1 bug: the leading shield must be RELEASES (UP),
        // the trailing restore must be PRESSES (DOWN). A stuck Ctrl + a bare
        // VK_RIGHT must reach the target as a plain arrow — not Ctrl+Right.
        var plan = InjectionShield.PlanVkSequence(
            new ushort[] { InjectionShield.VK_LCONTROL }, new ushort[] { 0x27 /* VK_RIGHT */ });
        Assert.Equal(new (ushort, bool)[]
        {
            (InjectionShield.VK_LCONTROL, true),  // shield: release held Ctrl
            (0x27, false),                        // arrow press
            (0x27, true),                         // arrow release
            (InjectionShield.VK_LCONTROL, false), // shield: restore held Ctrl
        }, plan.Select(e => (e.Vk, e.Up)).ToArray());
    }

    [Fact]
    public void Plan_held_ctrl_plus_select_chord_keeps_chord_intact()
    {
        // stuck Ctrl + Ctrl+Shift+Down: the shield releases the held Ctrl
        // first, the chord's OWN modifiers are pressed and released in
        // order, then the held Ctrl is restored. The target sees exactly
        // Ctrl+Shift+Down — never a doubled modifier.
        var plan = InjectionShield.PlanVkSequence(
            new ushort[] { InjectionShield.VK_LCONTROL },
            new ushort[] { 0x10 /*Shift*/, 0x11 /*Ctrl*/, 0x28 /*Down*/ });
        Assert.Equal(new (ushort, bool)[]
        {
            (InjectionShield.VK_LCONTROL, true),
            (0x10, false), (0x11, false), (0x28, false),
            (0x28, true), (0x11, true), (0x10, true),   // reverse-order releases
            (InjectionShield.VK_LCONTROL, false),
        }, plan.Select(e => (e.Vk, e.Up)).ToArray());
    }

    [Fact]
    public void Plan_without_shield_is_presses_then_reverse_releases()
    {
        var plan = InjectionShield.PlanVkSequence(
            Array.Empty<ushort>(), new ushort[] { 0x11, 0x41 });
        Assert.Equal(new (ushort, bool)[]
        {
            (0x11, false), (0x41, false), (0x41, true), (0x11, true),
        }, plan.Select(e => (e.Vk, e.Up)).ToArray());
    }

    // ── ContextSeedPolicy.EvaluateReadTrust (#91) ─────────────────────────

    [Fact]
    public void Trust_distrusts_the_observed_outlook_case()
    {
        // focused compose field (668) → top-level document (10,981)
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "toplevel", 10981, isCaretMovedReason: true, consecutiveDistrusts: 0));
        // and back
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("toplevel", 10981, "element", 668, true, 0));
    }

    [Fact]
    public void Trust_ignores_same_source_reads()
    {
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "element", 5000, true, 0));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("toplevel", 10981, "toplevel", 40, true, 0));
    }

    [Fact]
    public void Trust_ignores_small_discontinuities_across_sources()
    {
        // both views of the same short field: equivalent text, no teleport
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 120, "toplevel", 130, true, 0));
    }

    [Fact]
    public void Trust_ignores_first_read_no_baseline()
    {
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust(null, 0, "toplevel", 10981, true, 0));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("", 0, "element", 668, true, 0));
    }

    [Fact]
    public void Trust_accepts_focus_change_reads_unconditionally()
    {
        // a new target is expected on focus/foreground reasons — no gate
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "toplevel", 10981, isCaretMovedReason: false, consecutiveDistrusts: 0));
    }

    [Fact]
    public void Trust_adopts_permanent_source_switch_after_hysteresis()
    {
        // distrust #1 and #2...
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "toplevel", 10981, true, 0));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "toplevel", 10981, true, 1));
        // ...then the switch is judged permanent: accept and reset
        Assert.Equal(ContextSeedPolicy.ReadTrust.AcceptAfterHysteresis,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "toplevel", 10981, true, 2));
    }
}
