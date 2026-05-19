using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;

namespace Dasher.Windows.Engine;

public static class CommandRenderer
{
    private static readonly SolidColorBrush FallbackBrush = new(Color.FromArgb(255, 255, 255, 255));

    public static void Render(DrawingContext context, int[] commands, string[] strings, Size surfaceSize)
    {
        if (commands == null || commands.Length == 0) return;

        for (int i = 0; i + 5 < commands.Length; i += 6)
        {
            int op = commands[i];
            int a = commands[i + 1];
            int b = commands[i + 2];
            int c = commands[i + 3];
            int d = commands[i + 4];
            int argb = commands[i + 5];

            var color = ArgbToColor(argb);
            if (color == null) continue;

            switch (op)
            {
                case 0:
                    var bgBrush = new SolidColorBrush(color.Value);
                    context.DrawRectangle(bgBrush, null, new Rect(0, 0, surfaceSize.Width, surfaceSize.Height));
                    break;

                case 1:
                    {
                        bool filled = d == 1;
                        var brush = new SolidColorBrush(color.Value);
                        double r = Math.Max(1, c);
                        if (filled)
                        {
                            context.DrawEllipse(brush, null, new Point(a, b), r, r);
                        }
                        else
                        {
                            var pen = new Pen(brush, 1);
                            context.DrawEllipse(null, pen, new Point(a, b), r, r);
                        }
                    }
                    break;

                case 2:
                    {
                        var pen = new Pen(new SolidColorBrush(color.Value), 1);
                        context.DrawLine(pen, new Point(a, b), new Point(c, d));
                    }
                    break;

                case 3:
                    {
                        var rect = new Rect(
                            Math.Min(a, c), Math.Min(b, d),
                            Math.Abs(c - a), Math.Abs(d - b));
                        var pen = new Pen(new SolidColorBrush(color.Value), 1);
                        context.DrawRectangle(null, pen, rect);
                    }
                    break;

                case 4:
                    {
                        var rect = new Rect(
                            Math.Min(a, c), Math.Min(b, d),
                            Math.Abs(c - a), Math.Abs(d - b));
                        var brush = new SolidColorBrush(color.Value);
                        context.DrawRectangle(brush, null, rect);
                    }
                    break;

                case 5:
                    {
                        if (d >= 0 && d < strings.Length)
                        {
                            var brush = new SolidColorBrush(color.Value);
                            var formatted = new FormattedText(
                                strings[d],
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                new Typeface("Segoe UI"),
                                c,
                                brush);
                            context.DrawText(formatted, new Point(a, b));
                        }
                    }
                    break;
            }
        }
    }

    private static Color? ArgbToColor(int argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b2 = (byte)(argb & 0xFF);
        if (a == 0) return null;
        return Color.FromArgb(a, r, g, b2);
    }
}
