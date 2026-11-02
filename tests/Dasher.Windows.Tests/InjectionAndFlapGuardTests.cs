using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// #90 modifier-hygiene planner and #91 read-source flap distrust — the two
/// log-confirmed guards from Heide's v0.1.38 field logs.
/// </summary>
public class InjectionAndFlapGuardTests
{
    // ── InjectionShield.HeldModifiers (#90) ───────────────────────────────

    [Fact]
    public void Shield_lists_held_ctrl_alt_win_in_canonical_order()
    {
        var held = InjectionShield.HeldModifiers(ctrlDown: true, altDown: false, lwinDown: true, rwinDown: true);
        Assert.Equal(new ushort[] { InjectionShield.VK_CONTROL, InjectionShield.VK_LWIN, InjectionShield.VK_RWIN }, held);
    }

    [Fact]
    public void Shield_empty_when_no_modifiers_held()
    {
        Assert.Empty(InjectionShield.HeldModifiers(false, false, false, false));
    }

    [Fact]
    public void Shield_covers_alone_ctrl_the_reported_field_case()
    {
        // Heide's case: only Ctrl held (TouchMousePointer sticky after an
        // OmniClip shortcut) — the shield must release exactly that
        var held = InjectionShield.HeldModifiers(true, false, false, false);
        Assert.Single(held);
        Assert.Equal((ushort)0x11, held[0]);
    }

    // ── ContextSeedPolicy.SourceFlapDistrust (#91) ────────────────────────

    [Fact]
    public void Flap_distrusts_the_observed_outlook_case()
    {
        // focused compose field (668) → top-level document (10,981)
        Assert.True(ContextSeedPolicy.SourceFlapDistrust("element", 668, "toplevel", 10981));
        // and back
        Assert.True(ContextSeedPolicy.SourceFlapDistrust("toplevel", 10981, "element", 668));
    }

    [Fact]
    public void Flap_ignores_same_source_reads()
    {
        // same source, any length change (genuine edits, target switches of
        // the same path) — the policy's other guards handle those
        Assert.False(ContextSeedPolicy.SourceFlapDistrust("element", 668, "element", 5000));
        Assert.False(ContextSeedPolicy.SourceFlapDistrust("toplevel", 10981, "toplevel", 40));
    }

    [Fact]
    public void Flap_ignores_small_discontinuities_across_sources()
    {
        // both views of the same short field: equivalent text, no teleport
        Assert.False(ContextSeedPolicy.SourceFlapDistrust("element", 120, "toplevel", 130));
    }

    [Fact]
    public void Flap_ignores_first_read_no_baseline()
    {
        Assert.False(ContextSeedPolicy.SourceFlapDistrust(null, 0, "toplevel", 10981));
        Assert.False(ContextSeedPolicy.SourceFlapDistrust("", 0, "element", 668));
    }
}
