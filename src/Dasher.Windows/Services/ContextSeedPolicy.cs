using System;

namespace Dasher.Windows.Services;

/// <summary>What the caller should do after comparing the engine buffer with a target-field read.</summary>
public enum ContextSeedAction
{
    /// <summary>Re-seed the engine: <see cref="ContextSeedDecision.Text"/> at caret (UTF-16).</summary>
    Seed,

    /// <summary>Leave the engine alone (reason carries the diagnostic).</summary>
    Skip,
}

public readonly record struct ContextSeedDecision
{
    public ContextSeedAction Action { get; init; }
    public string Reason { get; init; }
    public string Text { get; init; }
    public int CaretUtf16 { get; init; }

    public static ContextSeedDecision Skip(string reason) => new() { Action = ContextSeedAction.Skip, Reason = reason };
    public static ContextSeedDecision Seed(string text, int caretUtf16) =>
        new() { Action = ContextSeedAction.Seed, Text = text, CaretUtf16 = caretUtf16 };
}

/// <summary>
/// Direct-entry context-seed decision (RFC 0015): compares the engine's edit
/// buffer against a target-field read and decides whether to re-seed. Pure
/// function over strings — unit-testable without UIA or an engine.
///
/// Every guard here exists because a re-seed is EXPENSIVE for the user:
/// dasher_seed_buffer rebuilds the model anchored at the caret
/// (CDasherModel::SetNode), which always recentres the view — a spurious
/// re-seed is the visible "canvas completely resets" bug. The guards keep
/// re-seeds to genuine context changes:
///
///  - sentence-equal skip: the engine's own output produced the target's
///    sentence, so it already sits at a matching position (with repeated
///    identical sentences, possibly a later twin than the target caret —
///    still the safer choice; see the repeated-boundary test). (Historically this
///    branch called dasher_set_offset with the SENTENCE-relative byte offset
///    while dasher_get_offset is BUFFER-absolute: the two coincide only until
///    the buffer spans a sentence boundary — then set_offset re-rooted the
///    model mid-buffer and reset the canvas on the first word of every new
///    sentence, and the moved cursor could corrupt subsequent output.)
///  - target-lag skip: a UIA read can lag the text we just injected. Within
///    a sentence the engine-ahead guard catches it, but a lag that crosses a
///    sentence terminator trims to different sentences ("Hello world" vs "")
///    and used to re-seed the STALE sentence over the live buffer — the
///    "resets every time you zoom into a period" report, including the
///    terminator being dropped and re-emitted into the target. Lag is
///    measured TAIL-anchored (the engine is sentence-windowed, so the
///    target's tail must match the engine's head — the field's earlier text
///    must not break the comparison).
///  - caret at a boundary mid-buffer → Seed(""): when the target caret sits
///    just past a terminator while the engine has output beyond it (the
///    trimmed target sentence is empty, the engine's is not), that is a
///    GENUINE re-anchor — the user clicked back to the boundary, exactly
///    like any other caret move. (Historically this path was skipped by
///    accident: StartsWith("") is trivially true in the engine-ahead guard,
///    leaving the engine stuck at the buffer end.)
/// </summary>
public static class ContextSeedPolicy
{
    /// <summary>
    /// Maximum full-text lag (UTF-16 code units, after normalisation) still
    /// attributed to a stale read.
    ///
    /// Trade-off, stated plainly: a genuine UIA lag is a character or two,
    /// but the cap is deliberately generous (32) because the two failure
    /// modes are asymmetric. A false SKIP only delays re-sync — deleting
    /// up to 32 trailing chars in the target, or switching to a field whose
    /// text is a ≤32-unit-shorter prefix of the engine buffer, keeps the
    /// stale context until real divergence breaks the prefix. A false SEED
    /// is an immediate visible canvas reset — the exact bug class this
    /// policy exists to prevent. When in doubt, skip.
    /// </summary>
    public const int MaxLagChars = 32;

    /// <summary>
    /// Minimum overlap (UTF-16 units) between the engine's head and the
    /// target read's tail for a "stale read" verdict. The floor only has to
    /// reject degenerate accidents: without it, engine "a test." matched a
    /// newly focused "Banana" field on a ONE-code-unit overlap ("a"), and
    /// the new field kept the previous field's context forever (every
    /// re-read, manual re-anchor included, skipped again) — greptile P1.
    /// Two matched units of the engine head at the read's tail are
    /// confirmation of the same text region; one is not. Deliberately NOT
    /// larger: a big floor (8+) left short sentences ("Hello.") without
    /// period-lag protection at all — the bug this policy exists to fix
    /// (greptile follow-up).
    /// </summary>
    public const int MinLagOverlapChars = 2;

    public static ContextSeedDecision Decide(string engineFullText, string targetFullText, int targetCaretUtf16)
    {
        var seedText = NormalizeInvisible(SentenceWindow.Trim(targetFullText, targetCaretUtf16).Text);
        var engineSentence = NormalizeInvisible(SentenceWindow.Trim(engineFullText, engineFullText.Length).Text);

        // Target-lag skip (before any sentence comparison): a UIA read can
        // predate our latest injected output — re-seeding from it would
        // resurrect stale text (canvas reset + the terminator re-emitted).
        // The engine buffer is SENTENCE-WINDOWED (seeded from the trimmed
        // sentence, not the whole field), so "the engine is ahead" means:
        // the engine with its last L chars removed is a SUFFIX OF THE READ
        // TRUNCATED AT THE CARET — the engine mirrors the text up to the
        // caret, so whatever the field carries AFTER the caret (greptile
        // P1: "Remaining text" mid-document) must not break the match.
        // The overlap must be at least MinLagOverlapChars (a one-unit
        // accident is not evidence of the same text region — greptile P1:
        // an unrelated new field kept the previous field's context), and
        // the pending L chars must actually be unreflected: if the read
        // already shows them right after the caret, the output IS
        // reflected and the caret simply moved back — a genuine re-anchor
        // (pinned by Caret_clicked_back_to_a_boundary_mid_buffer...), not
        // a stale read. \r is stripped so the engine's "\n" lines up with
        // Outlook's "\r\n"; an empty read is never treated as lag (a
        // freshly-focused empty field must clear stale context).
        var engineFull = NormalizeInvisible(engineFullText).Replace("\r", "");
        // Slice at the caret BEFORE normalising: normalisation removes
        // characters (\r, zero-width) and would shift the caret's index —
        // greptile P1: a mid-document CRLF read sliced at the original
        // caret included the following space and the lag check missed.
        var caret = Math.Clamp(targetCaretUtf16, 0, targetFullText.Length);
        var targetToCaret = NormalizeInvisible(targetFullText[..caret]).Replace("\r", "");
        var targetAfterCaret = NormalizeInvisible(targetFullText[caret..]).Replace("\r", "");

        // Paragraph-mark mirror (Nov 2026 field evidence, builds .34/.35):
        // a Dasher paragraph leaves the engine buffer ending "\n", and the
        // follow-up UIA read sits BEFORE or AT the paragraph mark — Outlook's
        // caret lands between the "\r" and "\n" of its representation, or the
        // read predates the mark entirely, or it has fully settled. All three
        // are the SAME text: the engine's own output IS the target's
        // paragraph. Re-seeding from such a read strips the newline the
        // engine just entered (buffer clear → canvas rebuild mid-zoom — the
        // "first paragraph goes crazy, the rest are fine" report: after the
        // strip the buffer no longer ends "\n", so later Enters compare
        // equal and behave). Tail-anchored like the lag check so earlier
        // field text the engine window doesn't carry cannot break it.
        // Length > 1: an engine buffer of just "\n" has an empty mirror, and
        // EndsWith("") is trivially true — that would skip EVERY read in the
        // exact greptile-P1 class MinLagOverlapChars documents (zero overlap
        // is not evidence of the same text), permanently, including the
        // manual re-anchor escape hatch. The degenerate buffer falls through
        // to the sentence logic, which handles it correctly.
        if (engineFull.Length > 1 && engineFull.EndsWith("\n", StringComparison.Ordinal) &&
            targetToCaret.EndsWith(engineFull[..^1], StringComparison.Ordinal))
            return ContextSeedDecision.Skip("paragraph-mark mirror — engine's newline is the target's; buffers in sync");

        // Lag loop. Paragraph-aware (Nov 2026): when the engine buffer ends
        // with its own "\n" and the target-to-caret ALREADY CONTAINS the
        // whole buffer plus text past it, the engine is provably BEHIND
        // (the user typed externally after our paragraph) — a coincidental
        // engine-prefix match ("Hello my " retyped on the new line) must not
        // read as "output not yet reflected" and swallow the real context.
        var targetHasWholeEngine = engineFull.EndsWith("\n", StringComparison.Ordinal) &&
                                   targetToCaret.Length > engineFull.Length &&
                                   targetToCaret.Contains(engineFull, StringComparison.Ordinal);
        for (var lag = 1; !targetHasWholeEngine && lag <= Math.Min(MaxLagChars, engineFull.Length - MinLagOverlapChars); lag++)
        {
            if (targetToCaret.EndsWith(engineFull[..^lag], StringComparison.Ordinal) &&
                !targetAfterCaret.StartsWith(engineFull[^lag..], StringComparison.Ordinal))
                return ContextSeedDecision.Skip($"target read lags engine output by {lag} char(s) — waiting for it to reflect");
        }

        // Sentence-equal skip: covers both the mid-sentence case and the
        // sentence-boundary case (both sides trim to ""). The engine is
        // already at a matching position — its own output produced a
        // sentence identical to the target's. With repeated identical
        // sentences the engine may be anchored at a LATER twin than the
        // target caret; the skip is still right (the old set_offset only
        // re-anchored correctly when the byte lengths coincided, and always
        // reset the canvas). set_offset is deliberately NOT called — see
        // the class doc.
        if (seedText == engineSentence)
            return ContextSeedDecision.Skip("sentence unchanged — engine already at a matching position (identical earlier sentence possible)");

        // Engine-ahead skip (sentence-level): the engine sentence starts with
        // the target sentence — our output is not in the target's UIA yet.
        // Re-seeding the shorter text would drop that output from the buffer.
        // (seedText must be non-empty: StartsWith("") is trivially true, and
        // an empty sentence with divergent full texts is a genuine context
        // change — e.g. focusing a fresh empty field must clear old context.)
        if (seedText.Length > 0 &&
            engineSentence.Length > seedText.Length && engineSentence.StartsWith(seedText, StringComparison.Ordinal))
            return ContextSeedDecision.Skip($"engine ahead of target read ({engineSentence.Length} vs {seedText.Length} chars) — output not yet reflected");

        // Benign divergence: a trailing space the engine hasn't absorbed yet.
        // The next character output settles both buffers.
        if (seedText.TrimEnd() == engineSentence.TrimEnd())
            return ContextSeedDecision.Skip("sentence matches modulo trailing whitespace");

        return ContextSeedDecision.Seed(seedText, seedText.Length);
    }

    /// <summary>
    /// Normalize invisible Unicode characters that rich-text editors (Outlook,
    /// Word, Gmail) insert into otherwise plain text: non-breaking spaces,
    /// zero-width spaces/joiners, BOM, narrow no-break spaces, figure spaces.
    /// These make otherwise-identical strings compare unequal and cause
    /// unnecessary re-seeds. Zero-width chars aren't classified as whitespace
    /// in .NET, so TrimEnd() alone can't catch them.
    /// </summary>
    public static string NormalizeInvisible(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\u00A0' or '\u202F' or '\u2007' or '\u2009': // NBSP, narrow NBSP, figure space, thin space
                    sb.Append(' ');
                    break;
                case '\u200B' or '\u200C' or '\u200D' or '\uFEFF': // zero-width space/ZWJ/ZWNJ/BOM → strip
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
