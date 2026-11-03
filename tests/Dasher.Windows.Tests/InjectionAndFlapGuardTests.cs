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

    // ── ContextSeedPolicy.EvaluateReadTrust (#91 + #97) ───────────────────

    [Fact]
    public void Trust_distrusts_the_observed_outlook_source_flap()
    {
        // focused compose field (647) → top-level document (11,454), same window
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, newCaret: 4651, sameTargetWindow: true, consecutiveDistrusts: 0, lastDistrustedSource: null));
        // and back
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("toplevel", 11454, "element", 647, 601, true, 0, null));
    }

    [Fact]
    public void Trust_gates_focus_churn_inside_the_same_window()
    {
        // THE #97 HOLE: the same flap arriving via a focus/foreground reason
        // within the same window must be distrusted too (v1 exempted these;
        // WebView2 focus churn carried incoherent reads straight to seeds)
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("toplevel", 11422, "element", 647, 601, sameTargetWindow: true, 0, null));
    }

    [Fact]
    public void Trust_accepts_a_genuinely_different_window()
    {
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, 4651, sameTargetWindow: false, 0, null));
    }

    [Fact]
    public void Trust_distrusts_the_observed_word_same_source_swings()
    {
        // #97 Word storm: same 'element' source, wildly different lengths
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "element", 13280, 102, true, 0, null));   // big delta
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 13280, "element", 14, 14, true, 0, null));     // big collapse
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 198, "element", 1225, 883, true, 0, null));    // ratio-only case (6.2x)
    }

    [Fact]
    public void Trust_distrusts_transient_garbage_reads()
    {
        // the 0/3/14-char reads with caret at the start against a long baseline
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "element", 3, 3, true, 0, null));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 3045, "element", 14, 14, true, 0, null));
        // same shape against a SHORT baseline is a plausible empty field — accept
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 12, "element", 3, 3, true, 0, null));
    }

    [Fact]
    public void Trust_distrusts_garbage_short_baseline_then_real_field()
    {
        // review loop 1 residual: a 14-char garbage read that slipped into
        // the baseline (caret 14 > the transient guard's caret bar) must
        // not let the REAL 887-char read straight through to a seed
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 14, "element", 887, 320, true, 0, null));
        // while a genuine small-field growth stays accepted (delta < 100)
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 12, "element", 48, 48, true, 0, null));
    }

    [Fact]
    public void Trust_ignores_same_source_and_normal_growth()
    {
        // typing: length changes by a char or two, same source
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 668, "element", 670, 670, true, 0, null));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("toplevel", 10981, "toplevel", 10983, 10983, true, 0, null));
        // equivalent text through the other source (short field, no discontinuity)
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("element", 120, "toplevel", 130, 130, true, 0, null));
    }

    [Fact]
    public void Trust_ignores_first_read_no_baseline()
    {
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust(null, 0, "toplevel", 10981, 10981, true, 0, null));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Accept,
            ContextSeedPolicy.EvaluateReadTrust("", 0, "element", 668, 668, true, 0, null));
    }

    [Fact]
    public void Trust_adopts_permanent_switch_after_hysteresis()
    {
        // distrust #1 and #2 (same distrusted source)...
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, 4651, true, 0, null));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, 4651, true, 1, "toplevel"));
        // ...then the switch is judged permanent: adopt on the 3rd qualifying read
        // (a different document opened in the SAME Word window converges here)
        Assert.Equal(ContextSeedPolicy.ReadTrust.AcceptAfterHysteresis,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, 4651, true, 2, "toplevel"));
    }

    [Fact]
    public void Trust_hysteresis_requires_a_consistent_distrusted_source()
    {
        // alternating off-baseline sources must not accumulate toward
        // adoption (toplevel/win32 mixing adopts neither)
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "toplevel", 11454, 4651, true, 2, "win32"));
        Assert.Equal(ContextSeedPolicy.ReadTrust.Distrust,
            ContextSeedPolicy.EvaluateReadTrust("element", 647, "win32", 5000, 5000, true, 2, "toplevel"));
    }
}
