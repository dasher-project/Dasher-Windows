using System;
using System.Linq;

namespace Dasher.Windows.Services;

/// <summary>
/// Trims target-field reads to the sentence around the caret (Heide's
/// suggestion, v5-aligned): the engine's language model only needs local
/// context for prediction, not the full email body. Seeding with the
/// sentence makes the shadow-compare stable during typing (sentence and
/// engine buffer grow together) and keeps re-seeds cheap (small text =
/// small model change). The selection-change watch was REMOVED with this:
/// per-keystroke re-reads caused visible canvas resets in Outlook whose
/// UIA provider fires TextSelectionChanged on every injected character and
/// returns inconsistent full-document reads (signatures, formatting, timing).
/// Pure function: unit-testable without UIA.
/// </summary>
public static class SentenceWindow
{
    /// <summary>Maximum lookback from the caret for a sentence boundary.</summary>
    public const int MaxLookback = 200;

    /// <summary>UTF-16 characters that end a sentence.</summary>
    private static readonly char[] Boundaries = { '.', '!', '?', '\n', '\r', ';', ':' };

    /// <summary>
    /// Extract the sentence context ending at the caret: from just after the
    /// last sentence boundary before the caret (or MaxLookback chars back,
    /// whichever is closer). The returned caret is at the END of the
    /// trimmed text (it ends at the original caret position).
    /// </summary>
    public static (string Text, int CaretUtf16) Trim(string text, int caretUtf16)
    {
        if (string.IsNullOrEmpty(text) || caretUtf16 <= 0)
            return ("", 0);

        // Clamp the caret to the text
        var caret = Math.Clamp(caretUtf16, 0, text.Length);

        // Find the lookback limit
        var lookbackStart = Math.Max(0, caret - MaxLookback);

        // Scan backwards for the last sentence boundary before the caret
        var start = lookbackStart;
        for (var i = caret - 1; i >= lookbackStart; i--)
        {
            if (Boundaries.Contains(text[i]))
            {
                start = i + 1;
                break;
            }
        }

        // Skip leading whitespace after the boundary
        while (start < caret && char.IsWhiteSpace(text[start]))
            start++;

        // Surrogate-pair guard (review): the 200-char cap can land on the LOW
        // half of a supplementary character — sliding one UTF-16 unit forward
        // prevents seeding an orphan half through the UTF-8 bridge (which
        // would make the shadow-compare never match again).
        if (start < caret && char.IsLowSurrogate(text[start]))
            start++;

        // End-side surrogate guard (review round 2): the caret can split a
        // pair (UIA ranges sometimes return mid-pair offsets) — step back so
        // the window doesn't end with a lone high surrogate.
        if (caret > start && char.IsHighSurrogate(text[caret - 1]))
            caret--;

        // Take from the sentence start to the caret
        var trimmed = text[start..caret];
        return (trimmed, trimmed.Length); // caret at end of trimmed text
    }

    /// <summary>
    /// Start/end of the adjacent sentence for caret moves and measured
    /// deletes (RFC 0021 move/delete migration). Same boundary set as
    /// <see cref="Trim"/> — one notion of "sentence" across seeding,
    /// selection, move and delete. Forward lands just AFTER the next
    /// boundary at/after <paramref name="from"/> (chained moves advance
    /// sentence by sentence, skipping a boundary the position sits on —
    /// mirroring ParagraphBound); backward lands at the START of the
    /// current/previous sentence. \r\n counts as ONE boundary (the
    /// ParagraphBound fix class — a caret must never land between the \r
    /// and the \n). Surrogate-safe.
    /// </summary>
    public static int SentenceBound(string text, int from, SelectDirection dir)
    {
        var i = Math.Clamp(from, 0, text.Length);
        if (dir == SelectDirection.Forward)
        {
            // skip a boundary unit the position sits on (chained moves);
            // \r\n is one unit
            if (i < text.Length && IsBoundary(text[i]))
                i += (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
            for (var k = i; k < text.Length; k++)
            {
                if (!IsBoundary(text[k])) continue;
                var after = (text[k] == '\r' && k + 1 < text.Length && text[k + 1] == '\n') ? k + 2 : k + 1;
                return Snap(text, after, dir);
            }
            return text.Length;
        }
        // Backward: land at the START of the sentence containing (or
        // preceding) the position; chained presses from a sentence start
        // must reach the PREVIOUS sentence (the stall class issue #84
        // fixed for paragraphs). \r\n steps back as one unit.
        if (i > 1 && text[i - 1] == '\n' && text[i - 2] == '\r')
            i -= 2; // sitting after a \r\n: step onto the unit start
        else if (i > 0 && IsBoundary(text[i - 1]))
            i--; // sitting after a single boundary: step onto it
        var prev = -1;
        for (var k = i - 1; k >= 0; k--)
        {
            if (IsBoundary(text[k])) { prev = k; break; }
        }
        var start = prev < 0 ? 0 : prev + 1;
        // sentence start skips the separator whitespace after its boundary —
        // bounded by the ORIGINAL position (not the stepped i): stepping
        // back onto a \r\n must still skip it (review loop 1: bounding by i
        // returned a caret position between the \r and the \n).
        while (start < from && start < text.Length && char.IsWhiteSpace(text[start])) start++;
        // chained: already AT this sentence's start → the previous one
        if (start == Math.Clamp(from, 0, text.Length) && prev > 0)
        {
            var before = -1;
            for (var k = prev - 1; k >= 0; k--)
            {
                if (IsBoundary(text[k])) { before = k; break; }
            }
            start = before < 0 ? 0 : before + 1;
            while (start < prev && char.IsWhiteSpace(text[start])) start++;
        }
        return Snap(text, start, dir);
    }

    private static bool IsBoundary(char c) => Boundaries.Contains(c);

    private static int Snap(string text, int i, SelectDirection dir) =>
        i > 0 && i < text.Length && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])
            ? (dir == SelectDirection.Backward ? i - 1 : i + 1)
            : i;
}
