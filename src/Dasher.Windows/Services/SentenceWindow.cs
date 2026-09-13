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
}
