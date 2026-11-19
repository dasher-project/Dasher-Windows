using System;
using System.Collections.Generic;
using System.Linq;

namespace Dasher.Windows.Speech;

/// <summary>
/// Line-oriented model of the pronunciation-override file
/// (%APPDATA%\Dasher\pronunciations.txt; grammar per the wrapper's
/// pronunciation.rs: `word = respell` or `word = /IPA/`, `#` comments,
/// multi-word left sides, both forms may coexist for one word). The
/// editor edits LINES so hand-written comments and blank lines survive
/// round-trips and Notepad co-editing stays friendly — the wrapper
/// re-reads the file whenever its mtime changes.
///
/// NOTE for editors built on this model: always re-read the file before
/// mutating (external edits shift line indexes and invalidate any cached
/// snapshot), and match removals by CONTENT, not by line index from a
/// stale parse.
/// </summary>
public static class PronunciationFile
{
    public sealed record Entry(int LineIndex, string Word, string Say);

    /// <summary>Entries in file order; comment/blank/malformed lines are
    /// skipped (count them with CountUnreadable to warn users).</summary>
    public static List<Entry> ParseEntries(string text)
    {
        var entries = new List<Entry>();
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryParseLine(lines[i], out var word, out var say))
                entries.Add(new Entry(i, word, say));
        }
        return entries;
    }

    /// <summary>Non-empty lines that are neither comments nor valid
    /// entries — dead lines the wrapper (and this editor) skips. Users
    /// should be told they exist (`botox =` typos etc.).</summary>
    public static int CountUnreadable(string text)
    {
        var lines = Lines(text);
        return lines.Count(l =>
        {
            var t = l.Trim();
            return t.Length > 0 && !t.StartsWith('#') && !TryParseLine(l, out _, out _);
        });
    }

    /// <summary>Parses `word = say`. Lenient like the wrapper: leading
    /// `#` is a comment, the FIRST `=` splits, both sides trimmed, empty
    /// sides rejected. (The wrapper does NOT treat `//` as a comment —
    /// neither do we, so a hand-written `// note = x` line stays visible
    /// as unreadable instead of silently vanishing.)</summary>
    public static bool TryParseLine(string? line, out string word, out string say)
    {
        word = say = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        if (t.StartsWith('#')) return false;
        var eq = t.IndexOf('=');
        if (eq <= 0) return false;
        word = t[..eq].Trim();
        say = t[(eq + 1)..].Trim();
        if (word.Length == 0 || say.Length == 0) return false;
        return true;
    }

    public static string FormatLine(string word, string say) => $"{word.Trim()} = {say.Trim()}";

    /// <summary>The wrapper collapses internal whitespace when matching
    /// keys (normalize_key): `san  rafael` and `san rafael` are the same
    /// override. Duplicate detection must agree or it misses pairs the
    /// engine will merge.</summary>
    public static string NormalizeKey(string word) =>
        string.Join(" ", (word ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Removes the entry lines selected by predicate over their
    /// ORIGINAL line index (as returned by ParseEntries on the SAME
    /// text), preserving every other line verbatim. When nothing matches
    /// (empty or out-of-range indexes) the original text is returned
    /// byte-identical. Otherwise output is line-normalized to LF —
    /// deliberate (wrapper accepts both; modern Notepad writes LF
    /// fine).</summary>
    public static string RemoveLines(string text, HashSet<int> lineIndexes)
    {
        var lines = Lines(text);
        var kept = lines.Where((_, i) => !lineIndexes.Contains(i)).ToList();
        if (kept.Count == lines.Length) return text;
        return string.Join("\n", kept) + "\n";
    }

    /// <summary>Appends one entry (both forms for a word may coexist by
    /// design — a second `botox` line is how a respell and an IPA form
    /// live side by side; appending preserves that).</summary>
    public static string AppendEntry(string text, string word, string say)
    {
        var t = (text ?? "").TrimEnd();
        return t.Length == 0 ? FormatLine(word, say) + "\n" : t + "\n" + FormatLine(word, say) + "\n";
    }

    /// <summary>True when the file already contains this word+say pair
    /// (case-insensitive; word compared with the wrapper's whitespace
    /// collapsing) — used to no-op duplicate adds.</summary>
    public static bool Contains(string text, string word, string say) =>
        ParseEntries(text).Any(e =>
            string.Equals(NormalizeKey(e.Word), NormalizeKey(word), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Say, say.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string[] Lines(string text) => (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    private static string Normalize(string text) => string.Join("\n", Lines(text)) + "\n";
}
