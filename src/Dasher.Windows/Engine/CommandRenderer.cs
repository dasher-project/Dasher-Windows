// Decodes the DasherCore draw command buffer into Avalonia DrawingContext calls.
//
// Command format: each command is 6 ints: [opcode, a, b, c, d, argb]
//
//   0: Clear screen          (a,b,c,d unused, argb = background colour)
//   1: Circle                (a=x, b=y, c=radius, d=1 filled / 0 stroked, argb)
//   2: Line                  (a=x1, b=y1, c=x2, d=y2, argb)
//   3: Rectangle outline     (a=x1, b=y1, c=x2, d=y2, argb)
//   4: Rectangle filled      (a=x1, b=y1, c=x2, d=y2, argb)
//   5: Text                  (a=x, b=y, c=fontSize, d=stringIndex, argb)
//   6: Set line width        (a=width, b,c,d unused)
//
// Render runs ~60x/s over hundreds of commands; brushes, pens and text
// layouts are cached (keyed by colour / colour+width / text+font+size+colour)
// so steady-state frames allocate nothing (#35).

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Dasher.Windows.Engine;

public static class CommandRenderer
{
    private const int CacheLimit = 1024;

    private static readonly Dictionary<int, IBrush> BrushCache = new();
    private static readonly Dictionary<(int argb, double width), IPen> PenCache = new();
    private static readonly Dictionary<(string text, string font, double size, int argb), FormattedText> TextCache = new();
    private static readonly Dictionary<string, Typeface> TypefaceCache = new();

    /// <summary>
    /// The typeface opcode-5 text is drawn with. Also used by the canvas
    /// text-measurement callback (DasherCore v0.2.4) so the engine lays out
    /// labels with the widths it will actually render at — measuring and
    /// drawing must never resolve different fonts.
    /// </summary>
    public static Typeface ResolveTypeface(string dasherFont)
    {
        var font = string.IsNullOrWhiteSpace(dasherFont) ? "Segoe UI" : dasherFont;
        if (!TypefaceCache.TryGetValue(font, out var typeface))
        {
            typeface = new Typeface(font);
            if (TypefaceCache.Count > 64) TypefaceCache.Clear();
            TypefaceCache[font] = typeface;
        }
        return typeface;
    }

    public static void Render(DrawingContext context, int[] commands, int commandCount,
        string[] strings, int stringCount, Size surfaceSize, string dasherFont = "")
    {
        if (commands == null || commandCount == 0) return;

        double currentLineWidth = 1;
        var cachedTypeface = ResolveTypeface(dasherFont);

        for (int i = 0; i + 5 < commandCount; i += 6)
        {
            int op = commands[i];
            int a = commands[i + 1];
            int b = commands[i + 2];
            int c = commands[i + 3];
            int d = commands[i + 4];
            int argb = commands[i + 5];

            byte alpha = (byte)((argb >> 24) & 0xFF);
            if (alpha == 0 && op != 6) continue;

            var color = Color.FromArgb(alpha, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

            switch (op)
            {
                case 0:
                    context.DrawRectangle(GetBrush(color), null, new Rect(0, 0, surfaceSize.Width, surfaceSize.Height));
                    break;
                case 1:
                    {
                        double r = Math.Max(1, c);
                        if (d == 1)
                            context.DrawEllipse(GetBrush(color), null, new Point(a, b), r, r);
                        else
                            context.DrawEllipse(null, GetPen(color, 1), new Point(a, b), r, r);
                    }
                    break;
                case 2:
                    context.DrawLine(GetPen(color, Math.Max(1, currentLineWidth)), new Point(a, b), new Point(c, d));
                    break;
                case 3:
                    {
                        var rect = new Rect(Math.Min(a, c), Math.Min(b, d), Math.Abs(c - a), Math.Abs(d - b));
                        context.DrawRectangle(null, GetPen(color, 1), rect);
                    }
                    break;
                case 4:
                    {
                        var rect = new Rect(Math.Min(a, c), Math.Min(b, d), Math.Abs(c - a), Math.Abs(d - b));
                        context.DrawRectangle(GetBrush(color), null, rect);
                    }
                    break;
                case 5:
                    {
                        if (d >= 0 && d < stringCount)
                        {
                            var formatted = GetFormattedText(strings[d], dasherFont, cachedTypeface, c, color);
                            context.DrawText(formatted, new Point(a, b));
                        }
                    }
                    break;
                case 6:
                    currentLineWidth = a;
                    break;
            }
        }
    }

    private static IBrush GetBrush(Color color)
    {
        var key = ToKey(color);
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            if (BrushCache.Count > CacheLimit) BrushCache.Clear();
            BrushCache[key] = brush;
        }
        return brush;
    }

    private static IPen GetPen(Color color, double width)
    {
        var key = (ToKey(color), width);
        if (!PenCache.TryGetValue(key, out var pen))
        {
            pen = new Pen(new SolidColorBrush(color), width);
            if (PenCache.Count > CacheLimit) PenCache.Clear();
            PenCache[key] = pen;
        }
        return pen;
    }

    private static FormattedText GetFormattedText(string text, string font, Typeface typeface, double size, Color color)
    {
        var key = (text, string.IsNullOrWhiteSpace(font) ? "" : font, size, ToKey(color));
        if (!TextCache.TryGetValue(key, out var formatted))
        {
            formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                new SolidColorBrush(color));
            // Labels persist across frames while visible; on overflow (font
            // change, deep zoom with many distinct strings) start fresh.
            if (TextCache.Count > CacheLimit) TextCache.Clear();
            TextCache[key] = formatted;
        }
        return formatted;
    }

    private static int ToKey(Color color) =>
        (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
}
