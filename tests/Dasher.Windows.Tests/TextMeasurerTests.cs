using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

public class TextMeasurerTests
{
    private static (double Width, double Height) FakeMeasure(string text, string font, double emSize)
        => (text.Length * emSize, emSize * 1.2);

    private static TextMeasurer New() => new(FakeMeasure);

    [Fact]
    public void Measure_success_returns_zero_per_dasher_h_contract()
    {
        var m = New();

        var rc = m.Measure("hello", "Segoe UI", 20, out var w, out var h);

        // dasher.h: 0 = success with sizes filled; non-zero = fall back.
        // v0.1.17 inverted this and the engine discarded every measurement.
        Assert.Equal(0, rc);
        Assert.True(w > 0);
        Assert.True(h > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Measure_bad_input_returns_non_zero(string? text)
    {
        var m = New();
        Assert.NotEqual(0, m.Measure(text, "Segoe UI", 20, out _, out _));
    }

    [Fact]
    public void Measure_zero_font_size_returns_non_zero()
    {
        var m = New();
        Assert.NotEqual(0, m.Measure("hello", "Segoe UI", 0, out _, out _));
    }

    [Fact]
    public void Measure_repeats_are_served_from_cache()
    {
        var m = New();
        m.Measure("cache me", "Segoe UI", 18, out var w1, out _);
        m.Measure("cache me", "Segoe UI", 18, out var w2, out _);

        Assert.Equal(w1, w2);
        Assert.Equal(1, m.CacheMisses);
    }

    [Fact]
    public void Measure_cache_is_keyed_by_text_font_and_size()
    {
        var m = New();
        m.Measure("x", "Segoe UI", 18, out _, out _);
        m.Measure("x", "Arial", 18, out _, out _);   // different font
        m.Measure("x", "Segoe UI", 24, out _, out _); // different size
        m.Measure("y", "Segoe UI", 18, out _, out _); // different text

        Assert.Equal(4, m.CacheMisses);
        m.Measure("x", "Segoe UI", 18, out _, out _);
        Assert.Equal(4, m.CacheMisses);
    }

    [Fact]
    public void Invalidate_forces_remeasure()
    {
        var m = New();
        m.Measure("z", "Segoe UI", 18, out _, out _);
        m.Invalidate();
        m.Measure("z", "Segoe UI", 18, out _, out _);

        Assert.Equal(2, m.CacheMisses);
    }
}
