using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Dasher.Windows.Services;

/// <summary>
/// Engine log ring buffer and PII scrubbing — extracted for testability.
/// RFC 0009: ring buffer captures engine state for crash reports.
/// </summary>
public class EngineLogRingBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<string> _lines = new();
    private int _bytes;
    private readonly int _maxLines;
    private readonly int _maxBytes;

    public EngineLogRingBuffer(int maxLines = 64, int maxBytes = 8 * 1024)
    {
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    public void Append(int level, string message)
    {
        var prefix = level switch { 0 => "[D]", 1 => "[I]", 2 => "[W]", 3 => "[E]", _ => "[X]" };
        var line = $"{prefix} {message}";

        lock (_lock)
        {
            _lines.AddLast(line);
            _bytes += line.Length;

            while (_lines.Count > _maxLines)
            {
                _bytes -= _lines.First!.Value.Length;
                _lines.RemoveFirst();
            }
            while (_bytes > _maxBytes && _lines.Count > 1)
            {
                _bytes -= _lines.First!.Value.Length;
                _lines.RemoveFirst();
            }
        }
    }

    public string Snapshot()
    {
        lock (_lock)
        {
            return string.Join("\n", _lines);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _lines.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
            _bytes = 0;
        }
    }
}

/// <summary>
/// PII scrubbing utilities (RFC 0009).
/// </summary>
public static class PiiScrubber
{
    private static readonly Regex UnixHome = new(@"(/Users/|/home/)([^/\\]+)", RegexOptions.Compiled);
    private static readonly Regex WindowsHome = new(@"C:\\Users\\([^\\]+)", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    public static string Scrub(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = UnixHome.Replace(s, "$1<user>");
        s = WindowsHome.Replace(s, @"C:\Users\<user>");
        s = Email.Replace(s, "<email>");
        return s;
    }
}
