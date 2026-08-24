using Dasher.Windows.Engine;

namespace Dasher.Windows.Tests;

public class TextUnitsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abc", 3)]
    [InlineData("\u00E9", 1)]
    [InlineData("e\u0301", 2)]
    [InlineData("caf\u00E9s", 5)]
    public void CountCodePoints_counts_bmp_characters(string text, int expected)
    {
        Assert.Equal(expected, TextUnits.CountCodePoints(text));
    }

    [Fact]
    public void CountCodePoints_counts_surrogate_pair_as_one_code_point()
    {
        // U+1F600 emoji: two UTF-16 units, one code point. Keyboard-mode
        // deletes must inject one backspace for it (RFC 0015 §5).
        Assert.Equal(1, TextUnits.CountCodePoints("\U0001F600"));
        Assert.Equal(2, "\U0001F600".Length);
    }

    [Fact]
    public void CountCodePoints_mixed_script_text()
    {
        // "a" + emoji + "b" + accented é = 4 code points, 5 UTF-16 units.
        var text = "a\U0001F600b\u00E9";
        Assert.Equal(5, text.Length);
        Assert.Equal(4, TextUnits.CountCodePoints(text));
    }
}
