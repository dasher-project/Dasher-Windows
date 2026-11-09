using System;
using System.Collections.Generic;
using Dasher.Windows.Services;
using Interop.UIAutomationClient;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// #121: sentence-accurate direct-mode deletes. UIA has no
/// TextUnit_Sentence, so the span is measured by the product's own
/// SentenceWindow (pure — bounds backward/forward, empty spans at the
/// edges) and selected by character-count endpoint moves from the document
/// range (driven here with a recording fake text provider). The STA/hwnd
/// discovery machinery around both is field-exercised; the unit semantics
/// are what these tests pin.
/// </summary>
public class TargetTextActionsTests
{
    private sealed record Op(string Kind, TextPatternRangeEndpoint? Endpoint, TextUnit? Unit, int Count);

    /// <summary>Fake IUIAutomationTextRange: records every measurement
    /// (MoveEndpointByUnit) and the Select, sharing the log AND the
    /// configured move counts across clones — the ops clone before
    /// measuring, so the Select and the served counts both land on the
    /// CLONE and per-instance state would never be seen. Counts come from
    /// a queue (default: echo the request — an exact provider).</summary>
    private sealed class RecordingRange : IUIAutomationTextRange
    {
        private readonly List<Op> _ops;
        private readonly int[] _moved;
        public RecordingRange(List<Op> ops, params int[] moved)
        {
            _ops = ops;
            _moved = moved;
        }

        public IUIAutomationTextRange Clone() => new RecordingRange(_ops, _moved);
        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            _ops.Add(new Op("move", endpoint, unit, count));
            return _moved.Length > 0 ? _moved[System.Math.Min(_moveCalls++, _moved.Length - 1)] : count;
        }
        public void Select() => _ops.Add(new Op("select", null, null, 0));

        private int _moveCalls;

        public int Compare(IUIAutomationTextRange range) => throw new NotSupportedException();
        public int CompareEndpoints(TextPatternRangeEndpoint src, IUIAutomationTextRange range, TextPatternRangeEndpoint target) => throw new NotSupportedException();
        public void ExpandToEnclosingUnit(TextUnit unit) => throw new NotSupportedException();
        public IUIAutomationTextRange FindAttribute(int attr, object val, int backward) => throw new NotSupportedException();
        public IUIAutomationTextRange FindText(string text, int backward, int ignoreCase) => throw new NotSupportedException();
        public object GetAttributeValue(int attr) => throw new NotSupportedException();
        public double[] GetBoundingRectangles() => throw new NotSupportedException();
        public IUIAutomationElement GetEnclosingElement() => throw new NotSupportedException();
        public string GetText(int maxLength) => throw new NotSupportedException();
        public int Move(TextUnit unit, int count) => throw new NotSupportedException();
        public void MoveEndpointByRange(TextPatternRangeEndpoint src, IUIAutomationTextRange range, TextPatternRangeEndpoint target) => throw new NotSupportedException();
        public void AddToSelection() => throw new NotSupportedException();
        public void RemoveFromSelection() => throw new NotSupportedException();
        public void ScrollIntoView(int alignToTop) => throw new NotSupportedException();
        public IUIAutomationElementArray GetChildren() => throw new NotSupportedException();
    }

    private static readonly TextPatternRangeEndpoint Start =
        TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
    private static readonly TextPatternRangeEndpoint End =
        TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;

    // ── SentenceSpanOffsets: the measured bounds ──────────────────────────

    [Fact]
    public void Sentence_span_backward_runs_from_sentence_start_to_caret()
    {
        //                    0123456789012345678901234567890123
        var text = "Alpha beta. Gamma delta. Epsilon.";
        // caret 18 sits inside "Gamma delta." (sentence 12..23)
        Assert.Equal((12, 18), TargetTextActions.SentenceSpanOffsets(text, 18, forward: false));
    }

    [Fact]
    public void Sentence_span_forward_runs_from_caret_past_next_boundary()
    {
        var text = "Alpha beta. Gamma delta. Epsilon.";
        // forward from 18 stops just past the '.' at 23 (at 24) — same
        // semantics as the editor-mode DeleteBounds sentence case
        Assert.Equal((18, 24), TargetTextActions.SentenceSpanOffsets(text, 18, forward: true));
    }

    [Fact]
    public void Sentence_span_at_document_edges_is_empty()
    {
        var text = "Alpha beta. Gamma delta.";
        Assert.Equal((0, 0), TargetTextActions.SentenceSpanOffsets(text, 0, forward: false));
        Assert.Equal((text.Length, text.Length), TargetTextActions.SentenceSpanOffsets(text, text.Length, forward: true));
        // and an empty document measures empty both ways (no paragraph
        // fallback — that would delete more than asked)
        Assert.Equal((0, 0), TargetTextActions.SentenceSpanOffsets("", 0, forward: true));
    }

    [Fact]
    public void Sentence_span_backward_from_sentence_start_reaches_previous_one()
    {
        var text = "Alpha beta. Gamma delta. Epsilon.";
        // the chained-move semantics (issue #84 class): backward from a
        // sentence START deletes the PREVIOUS sentence, not nothing
        Assert.Equal((0, 12), TargetTextActions.SentenceSpanOffsets(text, 12, forward: false));
    }

    // ── SelectSpanByOffsets: the selection over a fake provider ───────────

    [Fact]
    public void Span_builds_by_character_moves_then_selects()
    {
        var ops = new List<Op>();
        var applied = TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops), documentLength: 33, start: 12, end: 24, () => false);
        Assert.True(applied);
        Assert.Equal(new[]
        {
            new Op("move", End, TextUnit.TextUnit_Character, 12 - 33), // [0,33] → [0,12]
            new Op("move", Start, TextUnit.TextUnit_Character, 12),     // [0,12] → [12,12]
            new Op("move", End, TextUnit.TextUnit_Character, 12),       // [12,12] → [12,24]
            new Op("select", null, null, 0),
        }, ops);
    }

    [Fact]
    public void Span_provider_that_clamps_a_move_never_selects()
    {
        // a provider that swallows part of the move (rich-text quirks,
        // hidden chars) must fail the action — selecting a WRONG span is
        // worse than falling back
        var ops = new List<Op>();
        var applied = TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops, moved: -20), documentLength: 33, start: 12, end: 24, () => false);
        Assert.False(applied);
        Assert.Equal(new Op("move", End, TextUnit.TextUnit_Character, 12 - 33), ops[0]);
        Assert.Single(ops); // no further moves, no select
    }

    [Fact]
    public void Span_cancelled_before_select_discards_the_result()
    {
        // the double-mutation guard: a superseding action (or the timeout)
        // must stop the Select landing — measured but discarded
        var ops = new List<Op>();
        var applied = TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops), documentLength: 33, start: 12, end: 24, () => true);
        Assert.False(applied);
        Assert.Equal(3, ops.Count); // the three moves, no select
    }

    [Fact]
    public void Span_degenerate_or_out_of_bounds_selects_nothing()
    {
        var ops = new List<Op>();
        Assert.False(TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops), 33, 12, 12, () => false));
        Assert.False(TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops), 33, -1, 5, () => false));
        Assert.False(TargetTextActions.SelectSpanByOffsets(
            new RecordingRange(ops), 33, 30, 40, () => false));
        Assert.Empty(ops);
    }

    // ── ExtendSelectionByUnit: the #88/#89 word/paragraph machinery ───────

    [Fact]
    public void Word_and_paragraph_units_keep_their_88_89_measurement()
    {
        var wordOps = new List<Op>();
        Assert.True(TargetTextActions.ExtendSelectionByUnit(
            new RecordingRange(wordOps), TargetTextActions.Unit.Word, forward: false, () => false));
        Assert.Equal(new[]
        {
            new Op("move", Start, TextUnit.TextUnit_Word, -1),
            new Op("select", null, null, 0),
        }, wordOps);

        var paraOps = new List<Op>();
        Assert.True(TargetTextActions.ExtendSelectionByUnit(
            new RecordingRange(paraOps), TargetTextActions.Unit.Paragraph, forward: true, () => false));
        Assert.Equal(new[]
        {
            new Op("move", End, TextUnit.TextUnit_Paragraph, 1),
            new Op("select", null, null, 0),
        }, paraOps);
    }

    [Fact]
    public void Extend_at_document_edge_moves_zero_and_never_selects()
    {
        var ops = new List<Op>();
        Assert.False(TargetTextActions.ExtendSelectionByUnit(
            new RecordingRange(ops, moved: 0), TargetTextActions.Unit.Paragraph, forward: true, () => false));
        Assert.Equal(new[] { new Op("move", End, TextUnit.TextUnit_Paragraph, 1) }, ops);
    }
}

/// <summary>
/// Source-contract for the #121 dispatch wiring (the SelectWiringContractTests
/// pattern: three review loops shipped green suites over dead wiring —
/// structural pins catch that class before a reviewer does).
/// </summary>
public class SentenceDeleteContractTests
{
    [Fact]
    public void Direct_mode_delete_tries_the_sentence_span_before_the_paragraph_rung()
    {
        var src = ReadMainWindow();
        var deleteIdx = src.IndexOf("else // delete", StringComparison.Ordinal);
        Assert.True(deleteIdx >= 0, "delete branch not found");
        var sentenceIdx = src.IndexOf("SelectSentenceSpanAsync", deleteIdx, StringComparison.Ordinal);
        var paragraphIdx = src.IndexOf("chordDist == Services.SelectDistance.Paragraph", deleteIdx, StringComparison.Ordinal);
        var chordsIdx = src.IndexOf("DeleteKeystrokes(chordDist, dir)", deleteIdx, StringComparison.Ordinal);
        Assert.True(sentenceIdx > deleteIdx, "the sentence rung must exist in the delete branch");
        Assert.True(paragraphIdx > sentenceIdx, "the sentence rung must run BEFORE the paragraph fallback");
        Assert.True(chordsIdx > paragraphIdx, "the chord composition is the last rung");
    }

    [Fact]
    public void Sentence_delete_carries_its_own_hygiene_marker_and_telemetry()
    {
        var src = ReadMainWindow();
        Assert.Contains("ModifierHygiene.AssertClean(\"sentence-delete\")", src);
        Assert.Contains("via=uia-select", src);
        Assert.Contains("via=uia-select,empty=yes", src);
    }

    private static string ReadMainWindow()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        var path = System.IO.Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("repo root not found"),
            "src", "Dasher.Windows", "Views", "MainWindow.axaml.cs");
        return System.IO.File.ReadAllText(path);
    }
}
