using System;

namespace Dasher.Windows.Services;

/// <summary>
/// Editor-mode ⌫ / ← / → key semantics (the strip buttons on the message
/// pane). Pure functions over (text, caret) so the surrogate handling is
/// unit-testable — backspacing over an emoji (a constant in AAC messaging)
/// must delete the WHOLE pair: an orphaned half-surrogate seeds through the
/// UTF-8 bridge as U+FFFD and permanently breaks the editor shadow-compare
/// (the corruption class SentenceWindow and TextUnits defend against; review
/// loop 1 on the editing-keys PR).
/// </summary>
public static class EditorKeyActions
{
    /// <summary>Delete the code point before the caret. Returns the new
    /// (text, caret); unchanged when the caret is at the start.</summary>
    public static (string Text, int Caret) Backspace(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0) return (text, caret);
        var caretClamped = Math.Clamp(caret, 0, text.Length);
        // Two cases delete TWO units (deleting one would orphan the other
        // half — the U+FFFD corruption this class exists to prevent):
        //  - the unit before the caret is the LOW half of a pair: the pair
        //    occupies caret-2, caret-1;
        //  - the caret itself is mid-pair (a retained index straddling a
        //    pair — engine caret preservation can produce this): the pair
        //    occupies caret-1, caret.
        if (caretClamped >= 2 && char.IsLowSurrogate(text[caretClamped - 1]))
            return (text.Remove(caretClamped - 2, 2), caretClamped - 2);
        if (caretClamped < text.Length && char.IsLowSurrogate(text[caretClamped]))
            return (text.Remove(caretClamped - 1, 2), caretClamped - 1);
        return (text.Remove(caretClamped - 1, 1), caretClamped - 1);
    }

    /// <summary>Move the caret by UTF-16 units, never landing on the low
    /// half of a surrogate pair (a mid-pair caret feeds the engine a
    /// mid-codepoint byte offset). Clamped to [0, length]. Returns the
    /// clamped input when the move would be a no-op.</summary>
    public static int MoveCaret(string text, int caret, int delta)
    {
        if (string.IsNullOrEmpty(text) || delta == 0)
            return string.IsNullOrEmpty(text) ? 0 : Math.Clamp(caret, 0, text.Length);

        var target = Math.Clamp(Math.Clamp(caret, 0, text.Length) + delta, 0, text.Length);
        // Step over (not onto) a low surrogate: moving right skips it
        // forward, moving left retreats before its high half.
        if (target > 0 && target < text.Length && char.IsLowSurrogate(text[target]))
            target = Math.Clamp(target + (delta < 0 ? -1 : 1), 0, text.Length);
        return target;
    }
}
