using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Gecko RangeFromPoint round-down correction (Firefox/Thunderbird "canvas
/// jumps and doesn't zoom"): the caret point sits on a character boundary
/// and Gecko's RangeFromPoint returns the single character CONTAINING the
/// point — the char before the boundary — so the Start-side measurement
/// lands one behind on every read and the engine offset steps backward
/// per keystroke. The correction advances to the char's End, gated on
/// Mozilla window class + single-char range shape.
/// </summary>
public class GeckoCaretRoundDownTests
{
    [Theory]
    [InlineData(0)]   // caret after first char of "O"
    [InlineData(1)]   // "OK"
    [InlineData(41)]  // mid-text insert position
    public void Mozilla_single_char_range_advances_one(int measuredStart)
    {
        Assert.Equal(measuredStart + 1,
            TargetContextReader.AdjustCaretForProviderRoundDown(mozillaWindow: true, rangeIsSingleChar: true, measuredStart: measuredStart));
    }

    [Fact]
    public void Non_mozilla_never_adjusts()
    {
        Assert.Equal(5, TargetContextReader.AdjustCaretForProviderRoundDown(false, true, 5));
    }

    [Fact]
    public void Mozilla_degenerate_or_multi_char_range_never_adjusts()
    {
        // Degenerate (End==Start) and multi-char shapes are not the
        // "character containing the caret" round-down signature.
        Assert.Equal(5, TargetContextReader.AdjustCaretForProviderRoundDown(true, false, 5));
    }
}
