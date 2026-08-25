using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;

namespace Dasher.Windows.Engine;

/// <summary>
/// Measures label text for the engine's text-size callback
/// (DasherCore v0.2.4+). Return-code contract per dasher.h: 0 = success
/// (out sizes filled), non-zero = fall back to the engine's estimate.
/// The v0.1.17 build inverted this and the engine silently discarded
/// every measurement while re-calling ~21x per frame (#35).
/// </summary>
public sealed class TextMeasurer
{
    private readonly Func<string, string, double, (double Width, double Height)> _measure;
    private readonly Dictionary<(string text, string font, int size), (int w, int h)> _cache = new();

    /// <summary>Number of real measurements performed (cache misses).</summary>
    public int CacheMisses { get; private set; }

    public TextMeasurer() : this(MeasureWithFormattedText) { }

    public TextMeasurer(Func<string, string, double, (double Width, double Height)> measure)
    {
        _measure = measure;
    }

    public int Measure(string? text, string? font, int fontSize, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrEmpty(text) || fontSize <= 0)
            return 1;

        var key = (text, font ?? "", fontSize);
        if (!_cache.TryGetValue(key, out var measured))
        {
            var m = _measure(text, font ?? "", fontSize);
            measured = ((int)Math.Ceiling(m.Width), (int)Math.Ceiling(m.Height));
            CacheMisses++;
            if (_cache.Count > 4096) _cache.Clear();
            _cache[key] = measured;
        }

        width = measured.w;
        height = measured.h;
        return 0;
    }

    public void Invalidate() => _cache.Clear();

    private static (double Width, double Height) MeasureWithFormattedText(string text, string font, double emSize)
    {
        // Same font the canvas draws opcode-5 text with (user-selected
        // Dasher font, or Segoe UI), so layout matches rendering.
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            CommandRenderer.ResolveTypeface(font),
            emSize,
            Brushes.Black);
        return (formatted.Width, formatted.Height);
    }
}
