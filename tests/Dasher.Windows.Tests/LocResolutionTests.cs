using System.Globalization;
using Dasher.Windows.Services;
using Xunit;

namespace Dasher.Windows.Tests;

public class LocResolutionTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]   // exact specific-culture entry — must NOT fall to parent/en
    [InlineData("pt-PT", "pt-PT")]   // exact specific-culture entry
    [InlineData("en-GB", "en")]      // parent chain narrows to neutral
    [InlineData("en-US", "en")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt")]      // narrows to generic pt, matching the UI's satellite fallback
    [InlineData("de-DE", "de")]
    [InlineData("de", "de")]         // already neutral
    [InlineData("en", "en")]
    [InlineData("xx", "en")]         // unsupported -> English
    [InlineData("zh-TW", "en")]      // only zh-CN exists; do not silently substitute it
    public void ResolveCatalogueLocale_prefers_exact_specific_cultures(string input, string expected)
    {
        Assert.Equal(expected, Loc.ResolveCatalogueLocale(CultureInfo.GetCultureInfo(input)));
    }
}
