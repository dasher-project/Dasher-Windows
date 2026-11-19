using System;
using System.Collections.Generic;
using System.Linq;

namespace Dasher.Windows.Speech;

/// <summary>
/// Line-oriented model of the pronunciation-override file
/// (%APPDATA%\Dasher\pronunciations.txt; grammar per the wrapper's
/// docs/pronunciations.md: `word = respell` or `word = /IPA/`, `#`
/// comments, multi-word left sides, both forms may coexist for one
/// word). The editor edits LINES so hand-written comments and blank
/// lines survive round-trips and Notepad co-editing stays friendly —
/// the wrapper re-reads the file whenever its mtime changes.
/// </summary>
public static class PronunciationFile
{
    public sealed record Entry(int LineIndex, string Word, string Say);

    /// <summary>Entries in file order; comment/blank/malformed lines are
    /// skipped (their positions are preserved by the editor, which works
    /// on the raw line list).</summary>
    public static List<Entry> ParseEntries(string text)
    {
        var entries = new List<Entry>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryParseLine(lines[i], out var word, out var say))
                entries.Add(new Entry(i, word, say));
        }
        return entries;
    }

    /// <summary>Parses `word = say`. Lenient like the wrapper: leading
    /// `#` is a comment (also `//` for kindness), the FIRST `=` splits,
    /// both sides trimmed, empty sides rejected.</summary>
    public static bool TryParseLine(string? line, out string word, out string say)
    {
        word = say = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        if (t.StartsWith('#') || t.StartsWith("//")) return false;
        var eq = t.IndexOf('=');
        if (eq <= 0) return false;
        word = t[..eq].Trim();
        say = t[(eq + 1)..].Trim();
        if (word.Length == 0 || say.Length == 0) return false;
        return true;
    }

    public static string FormatLine(string word, string say) => $"{word.Trim()} = {say.Trim()}";

    /// <summary>Removes the entry lines selected by predicate over their
    /// ORIGINAL line index (as returned by ParseEntries on the same
    /// text), preserving every other line verbatim.</summary>
    public static string RemoveLines(string text, HashSet<int> lineIndexes)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Where((_, i) => !lineIndexes.Contains(i))) + "\n";
    }

    /// <summary>Appends one entry (both forms for a word may coexist by
    /// design — a second `botox` line is how a respell and an IPA form
    /// live side by side; appending preserves that).</summary>
    public static string AppendEntry(string text, string word, string say)
    {
        var t = (text ?? "").TrimEnd();
        return t.Length == 0 ? FormatLine(word, say) + "\n" : t + "\n" + FormatLine(word, say) + "\n";
    }

    /// <summary>True when the file already contains this exact word+say
    /// pair (case-insensitive) — the editor uses it to no-op duplicate
    /// adds instead of stacking copies.</summary>
    public static bool Contains(string text, string word, string say) =>
        ParseEntries(text).Any(e =>
            string.Equals(e.Word, word.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Say, say.Trim(), StringComparison.OrdinalIgnoreCase));
}
