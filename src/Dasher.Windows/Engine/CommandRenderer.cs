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

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Dasher.Windows.Engine;

public static class CommandRenderer
{
    public static void Render(DrawingContext context, int[] commands, string[] strings, Size surfaceSize, string dasherFont = "")
    {
        if (commands == null || commands.Length == 0) return;

        double currentLineWidth = 1;
        var fontFamily = string.IsNullOrWhiteSpace(dasherFont) ? "Segoe UI" : dasherFont;
        var cachedTypeface = new Typeface(fontFamily);

        for (int i = 0; i + 5 < commands.Length; i += 6)
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
                    context.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, surfaceSize.Width, surfaceSize.Height));
                    break;
                case 1:
                    {
                        double r = Math.Max(1, c);
                        if (d == 1)
                            context.DrawEllipse(new SolidColorBrush(color), null, new Point(a, b), r, r);
                        else
                            context.DrawEllipse(null, new Pen(new SolidColorBrush(color), 1), new Point(a, b), r, r);
                    }
                    break;
                case 2:
                    context.DrawLine(new Pen(new SolidColorBrush(color), Math.Max(1, currentLineWidth)), new Point(a, b), new Point(c, d));
                    break;
                case 3:
                    {
                        var rect = new Rect(Math.Min(a, c), Math.Min(b, d), Math.Abs(c - a), Math.Abs(d - b));
                        context.DrawRectangle(null, new Pen(new SolidColorBrush(color), 1), rect);
                    }
                    break;
                case 4:
                    {
                        var rect = new Rect(Math.Min(a, c), Math.Min(b, d), Math.Abs(c - a), Math.Abs(d - b));
                        context.DrawRectangle(new SolidColorBrush(color), null, rect);
                    }
                    break;
                case 5:
                    {
                        if (d >= 0 && d < strings.Length)
                        {
                            var formatted = new FormattedText(
                                strings[d],
                                CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                cachedTypeface,
                                c,
                                new SolidColorBrush(color));
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
}
