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

        var anchor = hadSelection ? selectionStart : Math.Clamp(selectionEnd, 0, text.Length);
        var active = hadSelection ? selectionEnd : anchor;
        var target = dist switch
        {
            SelectDistance.Char => Math.Clamp(active + (dir == SelectDirection.Backward ? -1 : 1), 0, text.Length),
            SelectDistance.Word => WordBound(text, active, dir),
            SelectDistance.Paragraph => ParagraphBound(text, active, dir),
            _ => active,
        };
        return target <= anchor ? (target, anchor) : (anchor, target);
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
        return i;
    }

    /// <summary>Start/end of the current paragraph (\n-delimited).</summary>
    internal static int ParagraphBound(string text, int from, SelectDirection dir)
    {
        var i = Math.Clamp(from, 0, text.Length);
        if (dir == SelectDirection.Forward)
        {
            var nl = text.IndexOf('\n', i);
            return nl < 0 ? text.Length : nl;
        }
        var prev = text.LastIndexOf('\n', Math.Max(0, i - 1));
        return prev < 0 ? 0 : prev + 1;
    }
}
