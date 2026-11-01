using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

/// <summary>
/// Direct-entry context-seed guards (RFC 0015). Each skip below maps to a
/// reported canvas-reset: dasher_seed_buffer rebuilds the model and recentres
/// the view, so a spurious Seed decision is the visible "canvas completely
/// resets" bug. See ContextSeedPolicy for the full rationale.
/// </summary>
public class ContextSeedPolicyTests
{
    // ── Bug: canvas resets after the FIRST WORD of each sentence ────────────

    [Fact]
    public void Sentence_match_with_boundary_spanning_buffer_skips_no_set_offset()
    {
        // Engine buffer spans a sentence boundary; the user just finished
        // zooming "The" (first word of the new sentence). Both sides trim to
        // "The". The old code compared the sentence-relative byte offset (3)
        // against dasher_get_offset()'s buffer-absolute value (16), found a
        // mismatch and called set_offset(3) — re-rooting the model mid-buffer
        // ("Hel") and resetting the canvas. The only correct action: skip.
        var d = ContextSeedPolicy.Decide("Hello world. The", "Hello world. The", 16);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Mid_sentence_match_still_skips()
    {
        // First sentence of a session: buffer == sentence, offsets would have
        // agreed in the old code too — skipping must remain the outcome.
        var d = ContextSeedPolicy.Decide("Hello wor", "Hello wor", 9);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    // ── Bug: canvas resets when you zoom into / type a period ───────────────

    [Fact]
    public void Stale_read_lagging_across_a_sentence_boundary_skips()
    {
        // The period was output and injected, but the UIA read predates it.
        // Sentence-trimming makes the sides disagree ("Hello world" vs "");
        // the old code re-seeded the STALE sentence, dropping the terminator
        // from the buffer (canvas reset + risk of re-emitting the period).
        var d = ContextSeedPolicy.Decide("Hello world.", "Hello world", 11);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Stale_read_with_earlier_target_text_still_skips()
    {
        // greptile P1: the engine is seeded with the SENTENCE WINDOW only,
        // while the target read carries earlier sentences before it. The
        // engine "Hello world." (after outputting '.') vs a stale read of
        // "Earlier sentence. Hello world" — a start-anchored prefix check
        // fails (the engine is SHORTER than the read) and the stale sentence
        // was re-seeded, dropping the terminator. The lag comparison must be
        // tail-anchored: the engine minus its last L chars is a SUFFIX of
        // the target read.
        var d = ContextSeedPolicy.Decide("Hello world.", "Earlier sentence. Hello world", 30);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Unrelated_new_field_is_not_read_as_lag()
    {
        // greptile P1: with a bare one-character overlap allowed, engine
        // "a test." head "a" matched a newly focused "Banana" field at
        // lag 7 — the new field kept the PREVIOUS field's context forever
        // (every re-read, manual re-anchor included, skipped again). A
        // stale-read verdict requires a minimum overlap
        // (MinLagOverlapChars); unrelated text must fall through to a
        // genuine re-seed.
        var d = ContextSeedPolicy.Decide("a test.", "Banana", 6);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("Banana", d.Text);
    }

    [Fact]
    public void Short_sentence_period_lag_still_skips()
    {
        // greptile follow-up P1: an 8-unit overlap floor left SHORT
        // sentences without period-lag protection — engine "Hello." vs a
        // read lagging the period ("Hello") fell through to
        // Seed("Hello"), the exact canvas-reset bug being fixed. The floor
        // is minimal (2) so short sentences keep the guard.
        var d = ContextSeedPolicy.Decide("Hello.", "Hello", 5);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Lag_comparison_anchors_at_the_caret_not_the_read_end()
    {
        // greptile P1: the field carries text AFTER the caret ("Remaining
        // text" mid-document). A read-end-anchored suffix check failed
        // ("Hello world" is not a suffix of "Hello world Remaining text")
        // and the stale sentence was re-seeded, dropping the period. The
        // engine mirrors the text UP TO THE CARET — the lag comparison
        // must truncate the read there.
        var d = ContextSeedPolicy.Decide("Hello world.", "Hello world Remaining text", 11);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Mid_document_crlf_read_lag_still_skips()
    {
        // greptile P1: normalising the WHOLE read before slicing at the
        // original caret shifted the cut point (the \r before the caret is
        // removed, so caret 14 landed one char late, including the space
        // after "Hello") and the lag check missed. Slice first, then
        // normalise each side.
        var d = ContextSeedPolicy.Decide("Hello.", "Earlier\r\nHello Remaining", 14);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Stale_read_with_earlier_target_text_catches_up_to_skip()
    {
        // The follow-up read reflects the period: both sides sit at the
        // boundary (empty sentence) — still a skip, via the sentence guard.
        var d = ContextSeedPolicy.Decide("Hello world.", "Earlier sentence. Hello world.", 31);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Stale_read_lagging_a_newline_crosses_crlf_difference()
    {
        // Engine emits "\n", Outlook shows "\r\n": the lag comparison must
        // ignore \r or it would misfire as divergence.
        var d = ContextSeedPolicy.Decide("Hello world.\nThe", "Hello world.\r\n", 14);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Lag_at_exactly_the_cap_is_treated_as_stale()
    {
        // Boundary pin for MaxLagChars: a strict-prefix read that lags by
        // exactly the cap still counts as UIA lag — skipping.
        var target = "Hello world.";
        var engine = target + new string('x', ContextSeedPolicy.MaxLagChars);
        var d = ContextSeedPolicy.Decide(engine, target, target.Length);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Lag_one_past_the_cap_re_seeds()
    {
        // …but one unit more is divergence, not lag: the guard must let the
        // sentence guards (and a possible re-seed) run. This is the only
        // test that fails if MaxLagChars is deleted or loosened.
        var target = "Hello world.";
        var engine = target + new string('x', ContextSeedPolicy.MaxLagChars + 1);
        var d = ContextSeedPolicy.Decide(engine, target, target.Length);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
    }

    [Fact]
    public void Divergent_texts_with_lag_within_cap_still_re_seed()
    {
        // Engine ahead by 21 units (within the cap) BUT the texts diverge
        // (not a prefix) — the cap is irrelevant here; divergence alone
        // means re-seed.
        var engine = "Hello world. " + new string('x', ContextSeedPolicy.MaxLagChars + 5);
        var d = ContextSeedPolicy.Decide(engine, "A completely different target", 29);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
    }

    // ── Pre-existing guards, pinned ──────────────────────────────────────────

    [Fact]
    public void Empty_sentence_at_boundary_skips()
    {
        // Both sides just output/landed the terminator: trim to "".
        var d = ContextSeedPolicy.Decide("Hello world.", "Hello world.", 12);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Engine_ahead_within_sentence_skips()
    {
        // Output injected but not yet reflected in the target read; the
        // engine sentence starts with the target sentence. Full texts DIVERGE
        // before the caret sentence so the earlier full-text lag guard cannot
        // catch this — pinning the sentence-level engine-ahead branch itself.
        var d = ContextSeedPolicy.Decide("Xyz. The quick", "Abc. The", 8);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Deleted_trailing_text_in_target_within_cap_is_skipped()
    {
        // The documented blind spot: the user deleted "The" in the target;
        // the engine buffer still ends with it, so the read looks like a
        // 3-unit lag (strict prefix) and is skipped until real divergence
        // arrives. Pinned deliberately — the reverse (re-seed) would be a
        // canvas reset on a deletion.
        var d = ContextSeedPolicy.Decide("Hello world. The", "Hello world. ", 13);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Trailing_whitespace_divergence_skips()
    {
        var d = ContextSeedPolicy.Decide("Hello world. The", "Hello world. The ", 17);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Invisible_chars_from_rich_text_do_not_force_a_reseed()
    {
        // NBSP in the target read where the engine emitted a regular space —
        // normalize to equal (zero-width stripping is covered separately by
        // Seed_text_is_normalized_before_seeding).
        var d = ContextSeedPolicy.Decide("Hi heide I", "Hi\u00A0heide I", 10);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    // ── Genuine context changes must still re-seed ──────────────────────────

    [Fact]
    public void Different_sentence_seeds_the_new_sentence()
    {
        // User clicked into another sentence / typed new text in the target.
        var d = ContextSeedPolicy.Decide("Hello world. The", "A different sentence entirely", 29);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("A different sentence entirely", d.Text);
        Assert.Equal(29, d.CaretUtf16);
    }

    [Fact]
    public void Freshly_focused_empty_field_seeds_empty()
    {
        // An empty read must clear stale engine context — never read as lag.
        var d = ContextSeedPolicy.Decide("old context", "", 0);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("", d.Text);
        Assert.Equal(0, d.CaretUtf16);
    }

    [Fact]
    public void Target_ahead_of_engine_seeds()
    {
        // The user typed directly in the target (not through Dasher).
        var d = ContextSeedPolicy.Decide("Hello world. The", "Hello world. The quick brown", 28);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("The quick brown", d.Text);
        Assert.Equal(15, d.CaretUtf16);
    }

    [Fact]
    public void Seed_text_is_normalized_before_seeding()
    {
        var d = ContextSeedPolicy.Decide("", "H\u00A0i there\u200B", 11);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("H i there", d.Text);
    }

    // ── Pinned edge behavior ─────────────────────────────────────────────────

    [Fact]
    public void Repeated_identical_sentences_skip_rather_than_re_anchor()
    {
        // Engine "Hi. Hi." is anchored after the FINAL period; the target
        // caret sits just after the FIRST one. Both sides trim to "" (empty
        // sentence at a boundary), so this is the sentence-equal skip: an
        // earlier identical boundary is deliberately NOT re-anchored to —
        // the old set_offset path only landed correctly when byte lengths
        // coincided and always reset the canvas.
        var d = ContextSeedPolicy.Decide("Hi. Hi.", "Hi. Hi.", 4);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Caret_clicked_back_to_a_boundary_mid_buffer_re_seeds_empty()
    {
        // Divergent full texts + empty target sentence (caret just past the
        // terminator while the engine has output beyond it): a genuine
        // re-anchor to where the user clicked — same as any other caret
        // move. Historically skipped BY ACCIDENT (StartsWith("") is always
        // true), leaving the engine stuck at the buffer end.
        var d = ContextSeedPolicy.Decide("Hello world. The", "Hello world. The", 13);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
        Assert.Equal("", d.Text);
        Assert.Equal(0, d.CaretUtf16);
    }

    // ---------------------------------------------------------------------
    // Paragraph-mark regression (first-paragraph "crazy canvas", Nov 2026):
    // engine emits "\n" -> VK_RETURN -> 500ms later the UIA read lands with
    // the caret AT/before the paragraph mark while the engine buffer is the
    // same text ending "\n". The sentence windows diverged -> re-seed
    // stripped the just-entered newline -> buffer cleared -> canvas rebuilt
    // mid-zoom. With gaze + auto-speed that is the runaway; with mouse, the
    // slowdown. (Outlook keyboard_debug.log evidence, builds .34 AND .35.)
    // ---------------------------------------------------------------------

    [Fact]
    public void Paragraph_mark_mirror_caret_before_mark_skips()
    {
        // Engine just entered the paragraph; Outlook's read still shows the
        // caret BEFORE the paragraph mark (\r representation).
        var d = ContextSeedPolicy.Decide(
            "Hello my name is Willy!\n",
            "Hello my name is Willy!\r\n",
            23); // caret before the \r (24 would be between \r and \n)
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Paragraph_mark_mirror_read_missing_the_newline_skips()
    {
        // UIA lag variant: the read predates the paragraph mark entirely.
        var d = ContextSeedPolicy.Decide(
            "Hello my name is Willy!\n",
            "Hello my name is Willy!",
            23);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Paragraph_mark_mirror_fully_reflected_read_skips()
    {
        // Settled variant: the read includes the newline and the caret sits
        // after it, with earlier field text the engine window does not carry.
        var d = ContextSeedPolicy.Decide(
            "Hello my name is Willy\n",
            "Earlier text. Hello my name is Willy\n",
            36);
        Assert.Equal(ContextSeedAction.Skip, d.Action);
    }

    [Fact]
    public void Target_has_whole_engine_plus_external_text_seeds_lag_disabler()
    {
        // The user typed on their PHYSICAL keyboard after Dasher's paragraph:
        // the read carries text the engine never emitted - a GENUINE context
        // update (RFC 0015 tier 2). The mirror must not over-skip this.
        var d = ContextSeedPolicy.Decide(
            "Hello my name is Willy!\n",
            "Hello my name is Willy!\r\nHello my &nail,",
            34); // caret after "Hello " on the new line
        Assert.Equal(ContextSeedAction.Seed, d.Action);
    }

    [Fact]
    public void Engine_buffer_of_just_a_newline_does_not_lock_the_policy()
    {
        // Review loop 1 P1: Enter as the FIRST Dasher output leaves the
        // buffer exactly "\n" - an empty mirror, and EndsWith("") is
        // trivially true. Without the Length > 1 conjunct the mirror would
        // skip EVERY read in that state, forever, including the manual
        // re-anchor escape hatch (the greptile-P1 class MinLagOverlapChars
        // documents: zero overlap is not evidence of the same text).
        var d = ContextSeedPolicy.Decide("\n", "Banana", 6);
        Assert.Equal(ContextSeedAction.Seed, d.Action);
    }
}
