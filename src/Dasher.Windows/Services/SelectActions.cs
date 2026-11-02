using System;
using System.Collections.Generic;

namespace Dasher.Windows.Services;

/// <summary>
/// RFC 0021: the select distances and their direction. Pure data shared by
/// the direct-mode keystroke mapper and the editor-mode measured selection.
/// </summary>
public enum SelectDistance
{
    Char,
    Word,
    Paragraph,
    Sentence,
    All,
}

public enum SelectDirection
{
    Backward,
    Forward,
}

/// <summary>
/// RFC 0021 select-action semantics (pure, unit-testable):
///  - Direct mode: the Windows keystroke for each distance/direction pair
///    (native OS selection, so the target's own Copy/Cut/Delete act on it
///    without further work).
///  - Editor mode: measured bounds over the pane text with an active-end
///    state machine ("backward/forward" extends from the end the frontend
///    last extended; cold start anchors at the caret).
/// RFC 0021 amendment (move/delete migration, Dasher-Windows #88): also
/// carries the caret-move/delete semantics — CaretKeystroke/
/// DeleteKeystrokes for direct mode, EditCaret/DeleteBounds for editor
/// mode.
/// </summary>
public static class SelectActions
{
    /// <summary>Direct-mode keystroke description for the pair. Select All
    /// is Ctrl+A; the rest are Shift(+Ctrl)+Arrow chords.</summary>
    public readonly record struct SelectKeystroke(
        bool Ctrl, bool Shift, ushort? Arrow, ushort? Letter);

    public static SelectKeystroke Keystroke(SelectDistance dist, SelectDirection dir)
    {
        const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_A = 0x41;
        return dist switch
        {
            SelectDistance.Char => new SelectKeystroke(false, true, dir == SelectDirection.Backward ? VK_LEFT : VK_RIGHT, null),
            SelectDistance.Word => new SelectKeystroke(true, true, dir == SelectDirection.Backward ? VK_LEFT : VK_RIGHT, null),
            SelectDistance.Paragraph => new SelectKeystroke(true, true, dir == SelectDirection.Backward ? VK_UP : VK_DOWN, null),
            SelectDistance.All => new SelectKeystroke(true, false, null, VK_A),
            _ => throw new ArgumentOutOfRangeException(nameof(dist)),
        };
    }

    /// <summary>Parse the action callback's attribute values. Unknown values
    /// return null (the engine validates no attributes for custom actions —
    /// a typo'd dist must not crash the frontend, it logs and no-ops).</summary>
    public static (SelectDistance Distance, SelectDirection Direction)? Parse(
        string? dist, string? forward)
    {
        var d = dist switch
        {
            "char" => (SelectDistance?)SelectDistance.Char,
            "word" => SelectDistance.Word,
            "paragraph" => SelectDistance.Paragraph,
            "sentence" => SelectDistance.Sentence,
            "all" => SelectDistance.All,
            _ => null,
        };
        if (d is null) return null;
        var f = forward switch
        {
            "yes" => (SelectDirection?)SelectDirection.Forward,
            "no" => SelectDirection.Backward,
            _ => null,
        };
        if (f is null) return null;
        return (d.Value, f.Value);
    }

    /// <summary>
    /// Editor-mode selection bounds over the pane text (pure). The active
    /// end is the end the last select extended (the anchor on cold start:
    /// caret with an empty selection). Returns the new (start, end) pair,
    /// UTF-16 units, ordered.
    /// </summary>
    public static (int Start, int End) EditorBounds(
        string text, int selectionStart, int selectionEnd,
        SelectDistance dist, SelectDirection dir, bool hadSelection)
    {
        // Editor-mode All is SelectAll regardless of direction (the RFC
        // table maps it to TextBox.SelectAll, not shift-to-boundary).
        if (dist == SelectDistance.All) return (0, text.Length);

        // Anchor/active (issue #84 + RFC 0021 state machine): this is a
        // STATELESS grow-only model — the anchor is the end OPPOSITE the
        // extension direction, the active is the end being extended. This
        // diverges from Windows Shift+Arrow (which shrinks on direction
        // reversal): a forward-then-backward sequence grows the selection
        // rather than collapsing it. That is a deliberate AAC choice — a
        // stateless pure function cannot track which end the user last
        // extended, and monotonic selection is safer for eye-gaze users
        // who cannot easily undo an accidental shrink. Direct mode uses
        // real Shift+Arrow keystrokes and DOES shrink (mode divergence
        // is documented in the RFC 0021 governance document's platform
        // table: dasher-project/governance/rfcs/0021-control-select-subtree.md).
        var anchor = hadSelection
            ? (dir == SelectDirection.Backward ? selectionEnd : selectionStart)
            : Math.Clamp(selectionEnd, 0, text.Length);
        var active = hadSelection
            ? (dir == SelectDirection.Backward ? selectionStart : selectionEnd)
            : anchor;
        var target = dist switch
        {
            SelectDistance.Char => StepCodePoint(text, active, dir),
            SelectDistance.Word => WordBound(text, active, dir),
            SelectDistance.Paragraph => ParagraphBound(text, active, dir),
            SelectDistance.Sentence => SentenceWindow.SentenceBound(text, active, dir),
            _ => active,
        };
        return target <= anchor ? (target, anchor) : (anchor, target);
    }


    /// <summary>Move by one CODE POINT, never landing on the low half of a
    /// surrogate pair (the corruption class EditorKeyActions defends
    /// against; a mid-pair selection boundary feeds the same class of bug
    /// downstream).</summary>
    private static int StepCodePoint(string text, int from, SelectDirection dir)
    {
        var i = Math.Clamp(from, 0, text.Length);
        if (dir == SelectDirection.Forward)
        {
            if (i >= text.Length) return text.Length;
            var step = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            return Math.Min(i + step, text.Length);
        }
        if (i <= 0) return 0;
        var back = i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]) ? 2 : 1;
        return Math.Max(i - back, 0);
    }

    /// <summary>Next/previous word boundary (skips the current run of
    /// word-chars then the run of separators, standard Ctrl+Arrow).</summary>
    internal static int WordBound(string text, int from, SelectDirection dir)
    {
        var i = Math.Clamp(from, 0, text.Length);
        if (dir == SelectDirection.Forward)
        {
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }
        else
        {
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        }
        return SnapToCodePoint(text, i, dir);
    }

    /// <summary>Snap a bound so it never splits a surrogate pair.</summary>
    private static int SnapToCodePoint(string text, int i, SelectDirection dir)
    {
        if (i > 0 && i < text.Length && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
            return dir == SelectDirection.Backward ? i - 1 : i + 1;
        return i;
    }

    /// <summary>Start/end of the current paragraph (\n-delimited). A chained
    /// forward-paragraph advances past exactly ONE separator (\r\n as a
    /// unit), so Ctrl+Shift+Down steps paragraph by paragraph — including
    /// blank ones — rather than stalling or swallowing them.</summary>
    internal static int ParagraphBound(string text, int from, SelectDirection dir)
    {
        var i = Math.Clamp(from, 0, text.Length);
        if (dir == SelectDirection.Forward)
        {
            // skip exactly one separator the active end sits on: a \n, or
            // the \r of a \r\n pair (the loop-2 version checked the wrong
            // position — text[i-1]=='\r' when text[i]=='\n' — and never
            // matched \r on \r\n text, so chained selects stalled there)
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2;
            else if (i < text.Length && text[i] == '\n') i++;
            var nl = text.IndexOf('\n', i);
            if (nl < 0) return text.Length;
            // \r\n: the selection must stop before the \r, not include it
            // (Avalonia TextBox inserts \r\n for Enter while the engine
            // pushes \n — review loop 1 on the select frontend).
            return nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl;
        }
        // Backward: skip one separator the active end sits immediately
        // after (mirrors the forward branch; without this, chained backward
        // selects stall at the same paragraph — issue #84)
        if (i > 0 && text[i - 1] == '\n')
        {
            i--; // step back onto the \n
            if (i > 0 && text[i - 1] == '\r') i--; // \r\n as a unit
        }
        // When the skip left i at 0 (leading blank line), the previous
        // paragraph starts at 0 — LastIndexOf at position 0 would re-find
        // the separator the skip just consumed (review loop 1 stall).
        var prev = i > 0 ? text.LastIndexOf('\n', i - 1) : -1;
        return SnapToCodePoint(text, prev < 0 ? 0 : prev + 1, dir);
    }

    // ── RFC 0021 amendment: move/delete (#88) ─────────────────────────────

    private const ushort VK_BACK = 0x08, VK_HOME = 0x24, VK_END = 0x23, VK_DELETE = 0x2E;

    /// <summary>Direct-mode caret-move keystroke for the pair (no Shift —
    /// moving the caret, not selecting). Sentence has no universal Windows
    /// chord: returns null and callers fall back to paragraph (logged) or
    /// a UIA unit move.</summary>
    public static SelectKeystroke? CaretKeystroke(SelectDistance dist, SelectDirection dir)
    {
        const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        return dist switch
        {
            SelectDistance.Char => new SelectKeystroke(false, false, dir == SelectDirection.Backward ? VK_LEFT : VK_RIGHT, null),
            SelectDistance.Word => new SelectKeystroke(true, false, dir == SelectDirection.Backward ? VK_LEFT : VK_RIGHT, null),
            SelectDistance.Paragraph => new SelectKeystroke(true, false, dir == SelectDirection.Backward ? VK_UP : VK_DOWN, null),
            SelectDistance.All => new SelectKeystroke(true, false, dir == SelectDirection.Backward ? VK_HOME : VK_END, null),
            _ => null, // Sentence: no chord — UIA or paragraph fallback
        };
    }

    /// <summary>Direct-mode delete plan: the ordered chords to send. A
    /// paragraph/sentence delete composes select-then-delete (two chords,
    /// the same envelope variance as the select chords themselves). The
    /// "principal key" rides the Arrow field (see SelectKeystroke).</summary>
    public static IReadOnlyList<SelectKeystroke> DeleteKeystrokes(SelectDistance dist, SelectDirection dir)
    {
        const ushort VK_UP = 0x26, VK_DOWN = 0x28, VK_A = 0x41;
        return dist switch
        {
            SelectDistance.Char => new[]
            {
                new SelectKeystroke(false, false, dir == SelectDirection.Backward ? VK_BACK : VK_DELETE, null),
            },
            SelectDistance.Word => new[]
            {
                new SelectKeystroke(true, false, dir == SelectDirection.Backward ? VK_BACK : VK_DELETE, null),
            },
            // sentence: no chord to bound it — select a paragraph instead
            // (the documented fallback; callers log the degradation)
            SelectDistance.Sentence or SelectDistance.Paragraph => new[]
            {
                new SelectKeystroke(true, true, dir == SelectDirection.Backward ? VK_UP : VK_DOWN, null),
                new SelectKeystroke(false, false, VK_DELETE, null),
            },
            SelectDistance.All => new[]
            {
                new SelectKeystroke(true, false, null, VK_A),
                new SelectKeystroke(false, false, VK_DELETE, null),
            },
            _ => Array.Empty<SelectKeystroke>(),
        };
    }

    /// <summary>Editor-mode caret move over the pane text (pure). All =
    /// document start/end; the rest measure with the same boundary
    /// functions the select path uses.</summary>
    public static int EditCaret(string text, int caret, SelectDistance dist, SelectDirection dir)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        return dist switch
        {
            SelectDistance.Char => StepCodePoint(text, i, dir),
            SelectDistance.Word => WordBound(text, i, dir),
            SelectDistance.Paragraph => ParagraphBound(text, i, dir),
            SelectDistance.Sentence => SentenceWindow.SentenceBound(text, i, dir),
            SelectDistance.All => (dir == SelectDirection.Forward ? text.Length : 0),
            _ => i,
        };
    }

    /// <summary>Editor-mode delete range (pure): the (start, end) UTF-16
    /// span the delete removes, ordered. The active end is the caret;
    /// backward deletes end AT the caret, forward deletes start AT it
    /// (nothing behind/beyond the caret is touched). NOTE: measured from
    /// the CARET, not an existing selection — replacing the selection is
    /// the plain Delete editing key's job (same deliberate divergence as
    /// the backspace editing key, EditorKeyActions.Backspace); the
    /// control-tree delete is a measured-distance operation by design.
    /// </summary>
    public static (int Start, int End) DeleteBounds(string text, int caret, SelectDistance dist, SelectDirection dir)
    {
        var i = Math.Clamp(caret, 0, text.Length);
        int start, end;
        switch (dist)
        {
            case SelectDistance.Char:
                var step = StepCodePoint(text, i, dir);
                (start, end) = dir == SelectDirection.Backward ? (step, i) : (i, step);
                break;
            case SelectDistance.Word:
                var w = WordBound(text, i, dir);
                (start, end) = dir == SelectDirection.Backward ? (w, i) : (i, w);
                break;
            case SelectDistance.Paragraph:
                var p = ParagraphBound(text, i, dir);
                (start, end) = dir == SelectDirection.Backward ? (p, i) : (i, p);
                break;
            case SelectDistance.Sentence:
                var s = SentenceWindow.SentenceBound(text, i, dir);
                (start, end) = dir == SelectDirection.Backward ? (s, i) : (i, s);
                break;
            case SelectDistance.All:
                (start, end) = (0, text.Length);
                break;
            default:
                (start, end) = (i, i);
                break;
        }
        return start <= end ? (start, end) : (end, start);
    }
}
