using System;

namespace Dasher.Windows.Services;

/// <summary>
/// RFC 0019 clause 7 — long-document seed cap. Seeding replaces the engine
/// buffer and rebuilds the model anchored at the caret; at extreme document
/// sizes that cost stops being acceptable per keystroke, so only a trailing
/// window of the text is seeded (predictions continue from the caret — the
/// head of a 500k-char document carries no more local signal than the tail).
/// Pure function: unit-testable without an engine.
/// </summary>
public static class EditorSeedPolicy
{
    /// <summary>Trailing UTF-16 window seeded for over-long documents.</summary>
    public const int MaxSeedUtf16 = 100_000;

    /// <returns>The text to seed, the caret offset within it (UTF-16), and
    /// whether truncation was applied (for diagnostics).</returns>
    public static (string Text, int CaretUtf16, bool Truncated) Clamp(string text, int caretUtf16)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxSeedUtf16)
            return (text, Math.Clamp(caretUtf16, 0, text?.Length ?? 0), false);

        var start = text.Length - MaxSeedUtf16;
        // A fixed UTF-16 cutoff can split a supplementary character (emoji,
        // CJK extensions): landing on the LOW half would seed an unpaired
        // surrogate through the UTF-8 bridge. Slide one unit forward.
        if (char.IsLowSurrogate(text[start])) start++;
        var seed = text[start..];
        var caret = Math.Clamp(caretUtf16 - start, 0, seed.Length);
        return (seed, caret, true);
    }
}
