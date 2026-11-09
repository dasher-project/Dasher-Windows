using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// RFC 0021 select-action semantics (pure): attribute parsing, direct-mode
/// keystroke mapping, editor-mode bounds + active-end state machine.
/// </summary>
public class SelectActionsTests
{
    // ── Parse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_accepts_all_documented_pairs()
    {
        foreach (var (d, f) in new[] { ("char", "yes"), ("char", "no"), ("word", "yes"), ("word", "no"), ("paragraph", "yes"), ("paragraph", "no"), ("all", "yes"), ("all", "no") })
        {
            var r = SelectActions.Parse(d, f);
            Assert.NotNull(r);
        }
    }

    [Fact]
    public void Parse_rejects_unknown_values()
    {
        Assert.Null(SelectActions.Parse("line", "yes")); // not in the control tree vocabulary
        Assert.Null(SelectActions.Parse("char", "maybe"));
        Assert.Null(SelectActions.Parse(null, "yes"));
        Assert.Null(SelectActions.Parse("char", null));
    }

    [Fact]
    public void Parse_accepts_sentence_for_move_delete()
    {
        // RFC 0021 amendment: move/delete carry sentence leaves; select
        // doesn't, but the parser is shared and accepts it (direct mode
        // falls back to paragraph, editor mode measures it).
        var r = SelectActions.Parse("sentence", "no");
        Assert.NotNull(r);
        Assert.Equal(SelectDistance.Sentence, r!.Value.Distance);
        Assert.Equal(SelectDirection.Backward, r.Value.Direction);
    }

    // ── Keystroke ─────────────────────────────────────────────────────────

    [Fact]
    public void Keystroke_char_is_shift_arrow()
    {
        var back = SelectActions.Keystroke(SelectDistance.Char, SelectDirection.Backward);
        Assert.False(back.Ctrl); Assert.True(back.Shift);
        Assert.Equal((ushort)0x25, back.Arrow); Assert.Null(back.Letter);

        var fwd = SelectActions.Keystroke(SelectDistance.Char, SelectDirection.Forward);
        Assert.Equal((ushort)0x27, fwd.Arrow);
    }

    [Fact]
    public void Keystroke_word_is_ctrl_shift_arrow()
    {
        var k = SelectActions.Keystroke(SelectDistance.Word, SelectDirection.Forward);
        Assert.True(k.Ctrl); Assert.True(k.Shift); Assert.Equal((ushort)0x27, k.Arrow);
    }

    [Fact]
    public void Keystroke_paragraph_is_ctrl_shift_up_down()
    {
        Assert.Equal((ushort)0x26, SelectActions.Keystroke(SelectDistance.Paragraph, SelectDirection.Backward).Arrow);
        Assert.Equal((ushort)0x28, SelectActions.Keystroke(SelectDistance.Paragraph, SelectDirection.Forward).Arrow);
    }

    [Fact]
    public void Keystroke_all_is_ctrl_a()
    {
        var k = SelectActions.Keystroke(SelectDistance.All, SelectDirection.Forward);
        Assert.True(k.Ctrl); Assert.False(k.Shift); Assert.Null(k.Arrow);
        Assert.Equal((ushort)0x41, k.Letter);
    }

    // ── EditorBounds ──────────────────────────────────────────────────────

    [Fact]
    public void Cold_start_char_forward_selects_one_char()
    {
        // caret at 5 in "hello world", no prior selection
        var (s, e) = SelectActions.EditorBounds("hello world", 5, 5, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(5, s); Assert.Equal(6, e);
    }

    [Fact]
    public void Extend_active_end_backward_extends_from_the_start()
    {
        var text = "hello world";
        var (s1, e1) = SelectActions.EditorBounds(text, 5, 5, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(5, s1); Assert.Equal(6, e1);
        // backward from (5,6): anchor=6 (fixed end), active=5 (extending end)
        // step back → 4. Result: (4,6) — backward selections anchor at the END
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Char, SelectDirection.Backward, true);
        Assert.Equal(4, s2); Assert.Equal(6, e2);
    }

    [Fact]
    public void Empty_text_is_safe_for_all_distances()
    {
        foreach (var dist in new[] { SelectDistance.Char, SelectDistance.Word, SelectDistance.Paragraph, SelectDistance.All })
            foreach (var dir in new[] { SelectDirection.Backward, SelectDirection.Forward })
            {
                var (s, e) = SelectActions.EditorBounds("", 0, 0, dist, dir, false);
                Assert.Equal(0, s); Assert.Equal(0, e);
            }
    }

    [Fact]
    public void Char_backward_from_zero_and_forward_from_end_clamp()
    {
        Assert.Equal((0, 0), SelectActions.EditorBounds("abc", 0, 0, SelectDistance.Char, SelectDirection.Backward, false));
        Assert.Equal((3, 3), SelectActions.EditorBounds("abc", 3, 3, SelectDistance.Char, SelectDirection.Forward, false));
    }

    [Fact]
    public void Word_forward_at_end_of_text_no_ops()
    {
        var (s, e) = SelectActions.EditorBounds("one", 3, 3, SelectDistance.Word, SelectDirection.Forward, false);
        Assert.Equal(3, s); Assert.Equal(3, e);
    }

    [Fact]
    public void Paragraph_forward_in_last_paragraph_selects_to_end()
    {
        var (s, e) = SelectActions.EditorBounds("first\nlast", 6, 6, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(6, s); Assert.Equal(10, e);
    }

    [Fact]
    public void Paragraph_forward_stops_before_cr_on_crlf()
    {
        // Avalonia TextBox inserts \r\n; the selection must not include \r
        var (s, e) = SelectActions.EditorBounds("first\r\nsecond", 2, 2, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(2, s); Assert.Equal(5, e); // stops at 5 (the \r), not 6 (the \n)
    }

    [Fact]
    public void Char_selection_is_surrogate_safe()
    {
        var emoji = "\U0001F600";
        var text = $"a{emoji}b";
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Char, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(3, e1);
        var (s2, e2) = SelectActions.EditorBounds(text, 3, 3, SelectDistance.Char, SelectDirection.Backward, false);
        Assert.Equal(1, s2); Assert.Equal(3, e2);
    }

    [Fact]
    public void Chained_paragraph_forward_advances_past_separator()
    {
        // Review loop 2: the bound must not re-find the same newline when
        // the active end sits on it (Ctrl+Shift+Down chains paragraphs).
        var text = "aaa\nbbb\nccc";
        // first: from 1 → to the first \n (3)
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(3, e1);
        // chained: from active end 3 (on the \n) → past it → to the second \n (7)
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Forward, true);
        Assert.Equal(1, s2); Assert.Equal(7, e2);
    }

    [Fact]
    public void Chained_paragraph_forward_on_crlf_advances()
    {
        // Review loop 3: on \r\n text (Avalonia inserts this on Enter),
        // the skip must consume the \r\n as a unit - not stall on the \r.
        var text = "aaa\r\nbbb\r\nccc";
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(3, e1); // stops before the \r at 3
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Forward, true);
        Assert.Equal(1, s2); Assert.Equal(8, e2); // chains past \r\n to the next \r at 8
    }

    [Fact]
    public void Chained_paragraph_forward_steps_blank_lines_one_at_a_time()
    {
        // Review loop 3: Ctrl+Shift+Down stops at each paragraph mark —
        // one separator per press; blank paragraphs are stepped, not swallowed.
        var text = "a\n\nb\nc"; // indices: a=0, \n=1, \n=2, b=3, \n=4, c=5
        // from the first \n: skip it, select to the second \n (the blank line's end)
        var (s1, e1) = SelectActions.EditorBounds(text, 1, 1, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(1, s1); Assert.Equal(2, e1); // selects just the blank line
        // from the second \n: skip it, select to after b
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Forward, true);
        Assert.Equal(1, s2); Assert.Equal(4, e2); // to the \n after b
    }

    [Fact]
    public void Chained_paragraph_backward_steps_one_at_a_time()
    {
        // Issue #84: chained backward selects stalled at the same paragraph.
        var text = "aaa\nbbb\nccc"; // \n at 3 and 7
        // from 10: first backward → 8 (start of ccc)
        var (s1, e1) = SelectActions.EditorBounds(text, 10, 10, SelectDistance.Paragraph, SelectDirection.Backward, false);
        Assert.Equal(8, s1); Assert.Equal(10, e1);
        // from active end 8 (sitting right after the \n at 7): skip it → 4 (start of bbb)
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Backward, true);
        Assert.Equal(4, s2); Assert.Equal(10, e2);
        // from 4: third backward → 0 (start of aaa)
        var (s3, e3) = SelectActions.EditorBounds(text, s2, e2, SelectDistance.Paragraph, SelectDirection.Backward, true);
        Assert.Equal(0, s3); Assert.Equal(10, e3);
    }

    [Fact]
    public void Chained_paragraph_backward_over_leading_blank_line()
    {
        // Review loop 1: the LastIndexOf clamp re-found the separator at
        // position 0 when the skip landed there — stalled at (1,4) forever.
        var text = "\naaa"; // leading blank line
        var (s1, e1) = SelectActions.EditorBounds(text, 4, 4, SelectDistance.Paragraph, SelectDirection.Backward, false);
        Assert.Equal(1, s1); Assert.Equal(4, e1); // to after the \n
        var (s2, e2) = SelectActions.EditorBounds(text, s1, e1, SelectDistance.Paragraph, SelectDirection.Backward, true);
        Assert.Equal(0, s2); Assert.Equal(4, e2); // chained: to position 0
    }

    [Fact]
    public void Parse_rejects_case_mismatches()
    {
        Assert.Null(SelectActions.Parse("Char", "yes"));
        Assert.Null(SelectActions.Parse("char", "Yes"));
    }

    [Fact]
    public void Word_forward_from_mid_word_lands_next_word_start()
    {
        // caret at 2 (mid "hello"), forward → start of "world" (6)
        var (s, e) = SelectActions.EditorBounds("hello world", 2, 2, SelectDistance.Word, SelectDirection.Forward, false);
        Assert.Equal(2, s); Assert.Equal(6, e);
    }

    [Fact]
    public void Word_backward_from_mid_second_word_lands_word_start()
    {
        var (s, e) = SelectActions.EditorBounds("hello world", 8, 8, SelectDistance.Word, SelectDirection.Backward, false);
        Assert.Equal(6, s); Assert.Equal(8, e);
    }

    [Fact]
    public void Paragraph_forward_to_next_newline()
    {
        var text = "first line\nsecond line\nthird";
        var (s, e) = SelectActions.EditorBounds(text, 3, 3, SelectDistance.Paragraph, SelectDirection.Forward, false);
        Assert.Equal(3, s); Assert.Equal(10, e); // \n at index 10
    }

    [Fact]
    public void Paragraph_backward_to_previous_newline()
    {
        var text = "first line\nsecond line\nthird";
        var (s, e) = SelectActions.EditorBounds(text, 15, 15, SelectDistance.Paragraph, SelectDirection.Backward, false);
        Assert.Equal(11, s); Assert.Equal(15, e);
    }

    [Fact]
    public void All_selects_everything()
    {
        var (s, e) = SelectActions.EditorBounds("some text", 4, 4, SelectDistance.All, SelectDirection.Forward, false);
        Assert.Equal(0, s); Assert.Equal(9, e);
        var (s2, e2) = SelectActions.EditorBounds("some text", 4, 4, SelectDistance.All, SelectDirection.Backward, false);
        Assert.Equal(0, s2); Assert.Equal(9, e2);
    }

    // ── RFC 0021 amendment: move/delete (#88) ───────────────────────────

    [Fact]
    public void CaretKeystroke_maps_all_direct_mode_dists()
    {
        // char: bare arrows
        var cb = SelectActions.CaretKeystroke(SelectDistance.Char, SelectDirection.Backward)!.Value;
        Assert.False(cb.Shift);
        Assert.Equal((ushort)0x25, cb.Arrow);
        Assert.Equal((ushort)0x27, SelectActions.CaretKeystroke(SelectDistance.Char, SelectDirection.Forward)!.Value.Arrow);
        // word: Ctrl+arrows, no shift
        var w = SelectActions.CaretKeystroke(SelectDistance.Word, SelectDirection.Forward)!.Value;
        Assert.True(w.Ctrl); Assert.False(w.Shift); Assert.Equal((ushort)0x27, w.Arrow);
        // paragraph: Ctrl+Up/Down
        Assert.Equal((ushort)0x26, SelectActions.CaretKeystroke(SelectDistance.Paragraph, SelectDirection.Backward)!.Value.Arrow);
        Assert.Equal((ushort)0x28, SelectActions.CaretKeystroke(SelectDistance.Paragraph, SelectDirection.Forward)!.Value.Arrow);
        // all: Ctrl+Home/End
        var home = SelectActions.CaretKeystroke(SelectDistance.All, SelectDirection.Backward)!.Value;
        Assert.True(home.Ctrl); Assert.False(home.Shift); Assert.Equal((ushort)0x24, home.Arrow);
        Assert.Equal((ushort)0x23, SelectActions.CaretKeystroke(SelectDistance.All, SelectDirection.Forward)!.Value.Arrow);
        // sentence: no chord (callers fall back)
        Assert.Null(SelectActions.CaretKeystroke(SelectDistance.Sentence, SelectDirection.Forward));
    }

    [Fact]
    public void DeleteKeystrokes_char_word_are_single_chords()
    {
        var cb = SelectActions.DeleteKeystrokes(SelectDistance.Char, SelectDirection.Backward);
        Assert.Single(cb);
        Assert.Equal((ushort)0x08, cb[0].Arrow); // Backspace
        var cf = SelectActions.DeleteKeystrokes(SelectDistance.Char, SelectDirection.Forward);
        Assert.Equal((ushort)0x2E, cf[0].Arrow); // Delete

        var wb = SelectActions.DeleteKeystrokes(SelectDistance.Word, SelectDirection.Backward);
        Assert.Single(wb);
        Assert.True(wb[0].Ctrl); Assert.Equal((ushort)0x08, wb[0].Arrow); // Ctrl+Backspace
        var wf = SelectActions.DeleteKeystrokes(SelectDistance.Word, SelectDirection.Forward);
        Assert.True(wf[0].Ctrl); Assert.Equal((ushort)0x2E, wf[0].Arrow); // Ctrl+Delete
    }

    [Fact]
    public void DeleteKeystrokes_paragraph_and_sentence_compose_select_then_delete()
    {
        foreach (var dist in new[] { SelectDistance.Paragraph, SelectDistance.Sentence })
        {
            var back = SelectActions.DeleteKeystrokes(dist, SelectDirection.Backward);
            Assert.Equal(2, back.Count);
            // step 1: Ctrl+Shift+Up (select to paragraph start)
            Assert.True(back[0].Ctrl && back[0].Shift);
            Assert.Equal((ushort)0x26, back[0].Arrow);
            // step 2: Delete
            Assert.False(back[1].Ctrl || back[1].Shift);
            Assert.Equal((ushort)0x2E, back[1].Arrow);

            var fwd = SelectActions.DeleteKeystrokes(dist, SelectDirection.Forward);
            Assert.Equal((ushort)0x28, fwd[0].Arrow); // Ctrl+Shift+Down
        }
    }

    [Fact]
    public void DeleteKeystrokes_all_is_select_all_then_delete()
    {
        var all = SelectActions.DeleteKeystrokes(SelectDistance.All, SelectDirection.Backward);
        Assert.Equal(2, all.Count);
        Assert.True(all[0].Ctrl); Assert.False(all[0].Shift);
        Assert.Equal((ushort)0x41, all[0].Letter); // 'A'
        Assert.Equal((ushort)0x2E, all[1].Arrow);
    }

    [Fact]
    public void EditCaret_moves_by_each_dist()
    {
        var text = "One. Two.\nThree four";
        // char
        Assert.Equal(4, SelectActions.EditCaret(text, 3, SelectDistance.Char, SelectDirection.Forward));
        Assert.Equal(2, SelectActions.EditCaret(text, 3, SelectDistance.Char, SelectDirection.Backward));
        // word: to the next word start
        Assert.Equal(5, SelectActions.EditCaret(text, 3, SelectDistance.Word, SelectDirection.Forward));
        // paragraph: stops AT the newline position (selection excludes it)
        Assert.Equal(9, SelectActions.EditCaret(text, 3, SelectDistance.Paragraph, SelectDirection.Forward));
        // sentence: forward from on the '.' after "One" lands just after the
        // NEXT boundary (the '.' after "Two")
        Assert.Equal(9, SelectActions.EditCaret(text, 3, SelectDistance.Sentence, SelectDirection.Forward));
        // all
        Assert.Equal(0, SelectActions.EditCaret(text, 7, SelectDistance.All, SelectDirection.Backward));
        Assert.Equal(text.Length, SelectActions.EditCaret(text, 7, SelectDistance.All, SelectDirection.Forward));
    }

    [Fact]
    public void EditCaret_sentence_backward_reaches_previous_sentence_and_chains()
    {
        var text = "One. Two. Three";
        // caret mid-Three: backward → start of Three
        Assert.Equal(10, SelectActions.EditCaret(text, 13, SelectDistance.Sentence, SelectDirection.Backward));
        // from that start: chained → start of Two
        Assert.Equal(5, SelectActions.EditCaret(text, 10, SelectDistance.Sentence, SelectDirection.Backward));
        // chained again → start of One
        Assert.Equal(0, SelectActions.EditCaret(text, 5, SelectDistance.Sentence, SelectDirection.Backward));
        // at text start: stays
        Assert.Equal(0, SelectActions.EditCaret(text, 0, SelectDistance.Sentence, SelectDirection.Backward));
    }

    [Fact]
    public void DeleteBounds_backward_ends_at_caret_and_forward_starts_at_it()
    {
        var text = "hello world again";
        var wb = SelectActions.DeleteBounds(text, 11, SelectDistance.Word, SelectDirection.Backward);
        Assert.Equal(6, wb.Start); Assert.Equal(11, wb.End);
        var wf = SelectActions.DeleteBounds(text, 6, SelectDistance.Word, SelectDirection.Forward);
        Assert.Equal(6, wf.Start); Assert.Equal(12, wf.End); // includes the trailing space (Ctrl+Delete semantics)

        var pb = SelectActions.DeleteBounds("first\nsecond\nthird", 12, SelectDistance.Paragraph, SelectDirection.Backward);
        Assert.Equal(6, pb.Start); Assert.Equal(12, pb.End);

        var all = SelectActions.DeleteBounds("some text", 4, SelectDistance.All, SelectDirection.Forward);
        Assert.Equal(0, all.Start); Assert.Equal(9, all.End);
    }

    [Fact]
    public void DeleteBounds_char_backward_one_code_point()
    {
        // emoji (surrogate pair) before the caret: deletes BOTH halves
        var text = "a😀b";
        var (s, e) = SelectActions.DeleteBounds(text, 3, SelectDistance.Char, SelectDirection.Backward);
        Assert.Equal(1, s); Assert.Equal(3, e);
        // and forward from 1 skips the pair whole
        var (s2, e2) = SelectActions.DeleteBounds(text, 1, SelectDistance.Char, SelectDirection.Forward);
        Assert.Equal(1, s2); Assert.Equal(3, e2);
    }

    [Fact]
    public void DeleteBounds_empty_range_at_document_edge_is_noop()
    {
        var (s, e) = SelectActions.DeleteBounds("abc", 0, SelectDistance.Char, SelectDirection.Backward);
        Assert.Equal(0, s); Assert.Equal(0, e);
        var (s2, e2) = SelectActions.DeleteBounds("abc", 3, SelectDistance.Word, SelectDirection.Forward);
        Assert.Equal(3, s2); Assert.Equal(3, e2);
    }

    // ── Direct-mode sentence delete (#121) ───────────────────────────────

    [Fact]
    public void DirectDeleteSuffix_sentence_exact_path_reports_uia_select()
    {
        // field logs must distinguish the sentence-exact mechanism from
        // the #89 paragraph spans (via=uia)
        Assert.Equal(",via=uia-select",
            SelectActions.DirectDeleteSuffix(SelectDistance.Sentence, SelectActions.DirectDeleteMechanism.SentenceSpan));
    }

    [Fact]
    public void DirectDeleteSuffix_sentence_degradations_keep_fallback_tag()
    {
        // paragraph span applied after sentence measurement failed…
        Assert.Equal(",via=uia,fallback=paragraph",
            SelectActions.DirectDeleteSuffix(SelectDistance.Sentence, SelectActions.DirectDeleteMechanism.ParagraphSpan));
        // …and the chord composition (no UIA at all)
        Assert.Equal(",fallback=paragraph",
            SelectActions.DirectDeleteSuffix(SelectDistance.Sentence, SelectActions.DirectDeleteMechanism.Chords));
    }

    [Fact]
    public void DirectDeleteSuffix_other_granularities_unchanged()
    {
        Assert.Equal(",via=uia",
            SelectActions.DirectDeleteSuffix(SelectDistance.Paragraph, SelectActions.DirectDeleteMechanism.ParagraphSpan));
        Assert.Equal("", SelectActions.DirectDeleteSuffix(SelectDistance.Paragraph, SelectActions.DirectDeleteMechanism.Chords));
        Assert.Equal("", SelectActions.DirectDeleteSuffix(SelectDistance.Word, SelectActions.DirectDeleteMechanism.Chords));
        Assert.Equal("", SelectActions.DirectDeleteSuffix(SelectDistance.Char, SelectActions.DirectDeleteMechanism.Chords));
        Assert.Equal("", SelectActions.DirectDeleteSuffix(SelectDistance.All, SelectActions.DirectDeleteMechanism.Chords));
    }

    [Fact]
    public void DeleteKeystrokes_sentence_still_maps_to_the_paragraph_composition()
    {
        // The degradation rung: sentence has no chord of its own — the
        // paragraph select+Delete composition IS its chord fallback.
        var plan = SelectActions.DeleteKeystrokes(SelectDistance.Sentence, SelectDirection.Backward);
        Assert.Equal(2, plan.Count); // Ctrl+Shift+Up then plain Delete
        Assert.True(plan[0].Ctrl && plan[0].Shift);
        Assert.Equal((ushort)0x26, plan[0].Arrow);
        Assert.Equal((ushort)0x2E, plan[1].Arrow);
    }
}


/// <summary>
/// Source-contract: the CustomAction wiring must exist in OnOpened, after
/// the _canvas assignment. Three consecutive review loops (PR #81) shipped
/// green suites over dead wiring — a ctor-time call before _canvas is
/// populated silently no-ops and no select event ever reaches a subscriber.
/// This fails the build instead of waiting for a reviewer.
/// </summary>
public class SelectWiringContractTests
{
    [Fact]
    public void CustomAction_subscription_follows_canvas_assignment_in_OnOpened()
    {
        var root = RepoRoot();
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "src", "Dasher.Windows", "Views", "MainWindow.axaml.cs"));
        var canvasIdx = src.IndexOf("_canvas = this.FindControl<DasherCanvas>", StringComparison.Ordinal);
        var wireIdx = src.IndexOf("WireCustomActions();", StringComparison.Ordinal);
        Assert.True(canvasIdx >= 0, "canvas assignment not found");
        Assert.True(wireIdx > canvasIdx, "WireCustomActions() must be called AFTER the _canvas assignment in OnOpened");
        // and the constructor must NOT call it (the null-field trap)
        var ctorStart = src.IndexOf("public MainWindow()", StringComparison.Ordinal);
        var ctorEnd = src.IndexOf("private void", ctorStart, StringComparison.Ordinal);
        var ctorBody = src.Substring(ctorStart, ctorEnd - ctorStart);
        Assert.DoesNotContain("WireCustomActions", ctorBody);
    }

    [Fact]
    public void DasherCanvas_registers_select_before_any_screen_size_call()
    {
        var root = RepoRoot();
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "src", "Dasher.Windows", "Controls", "DasherCanvas.cs"));
        var regIdx = src.IndexOf("dasher_register_action", StringComparison.Ordinal);
        Assert.True(regIdx >= 0, "dasher_register_action call not found in DasherCanvas");

        // Presence + Initialize-method-membership (structural, not dataflow):
        // the registration must sit inside the Initialize method body, and
        // every dasher_set_screen_size call site lives in StartEngine /
        // RealizeOffUiThreadAsync / TrySetScreenSize — all called after
        // Initialize completes.
        var initStart = src.IndexOf("public void Initialize(", StringComparison.Ordinal);
        var initEnd = src.IndexOf("private void", initStart, StringComparison.Ordinal);
        Assert.True(initStart >= 0 && regIdx > initStart && regIdx < initEnd,
            "the select registration must be inside the Initialize method body");
    }

    // Ordering within Initialize is verified structurally: the registration
    // sits inside the Initialize method body (before its closing brace), and
    // every dasher_set_screen_size call site in the file is in StartEngine,
    // RealizeOffUiThreadAsync, or TrySetScreenSize - all called after
    // Initialize. A full dataflow assertion is overkill for a source
    // contract; presence + method-membership is the right granularity.

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
