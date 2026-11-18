using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

// NOTE: shares the rust_tts_wrapper.dll native library with
// RustEngineSchemaContractTests — the collection keeps the classes off
// xunit's parallel lanes.

/// <summary>
/// Windows synthesis capability per sherpa model family (verified by live
/// probes at RustTtsWrapper.Bindings 1.13.2 — see AACTools/rust-tts-
/// wrapper#116): MMS, Kokoro, and VITS/Piper speak (VITS unlocked by the
/// 1.13.2 sherpa-g2p fix); Kitten still fails engine creation and Matcha
/// still fails tokenization. This gate keeps AAC users from downloading a
/// voice that cannot speak in this build.
/// </summary>
[Collection("rust-tts-native")]
public class SherpaCapabilityTests
{
    [Theory]
    [InlineData("mms")]
    [InlineData("kokoro")]
    [InlineData("vits")]
    [InlineData("VITS")]
    public void Verified_families_are_speakable(string family)
    {
        Assert.True(SherpaSynthesisCapability.IsSpeakable(family));
    }

    [Theory]
    [InlineData("kitten")]
    [InlineData("matcha")]
    public void Broken_families_are_gated(string family)
    {
        Assert.False(SherpaSynthesisCapability.IsSpeakable(family));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-family")]
    public void Unknown_or_missing_families_are_gated(string? family)
    {
        Assert.False(SherpaSynthesisCapability.IsSpeakable(family));
    }

    [Fact]
    public void Speakable_families_exist_in_the_live_registry()
    {
        // Full catalogue is 1770 models at 1.13.2 — a 400-limit sample only
        // catches 1 of 4 kokoro models, which made this test flaky and the
        // browser's list truncated. SearchLimit headroom matches the browser.
        var registry = RustTtsWrapper.TtsClient.SearchModels("", 5000);
        Assert.True(registry.Count >= 1500, $"expected the full catalogue, got {registry.Count}");
        foreach (var family in SherpaSynthesisCapability.SpeakableTypes)
        {
            Assert.Contains(registry, m => string.Equals(m.ModelType, family, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Row_helpers_format_names_sizes_and_subtitles()
    {
        var m = new RustTtsWrapper.TtsLocalModel
        {
            Id = "piper-en_US-john-medium",
            Name = "john",
            ModelType = "vits",
            Quality = "medium",
            FileSizeMb = 64.1,
            NumSpeakers = 1,
            License = "MIT",
        };
        Assert.Equal("John", SherpaModelBrowserWindow.DisplayName(m));
        Assert.Equal("MMS", SherpaModelBrowserWindow.DisplayName(new RustTtsWrapper.TtsLocalModel { Id = "x", Name = "", ModelType = "mms" }));
        Assert.Equal("MMS", SherpaModelBrowserWindow.DisplayName(new RustTtsWrapper.TtsLocalModel { Id = "x", Name = "unknown", ModelType = "mms" }));
        Assert.Equal("64.1 MB", SherpaModelBrowserWindow.FormatSize(64.1));
        Assert.Equal("1.2 GB", SherpaModelBrowserWindow.FormatSize(1228.8));
        Assert.Contains("Vits", SherpaModelBrowserWindow.BuildSubtitle(m));
        Assert.Contains("medium", SherpaModelBrowserWindow.BuildSubtitle(m));
        Assert.Contains("64.1 MB", SherpaModelBrowserWindow.BuildSubtitle(m));
        Assert.Contains("MIT", SherpaModelBrowserWindow.BuildSubtitle(m));
    }

    [Fact]
    public void InstalledModels_returns_only_actually_installed_models()
    {
        // Regression: the bindings' GetLocalModels returns the FULL catalogue
        // (~1700 entries) carrying Installed flags; the browser's
        // "Installed only" toggle is built from InstalledModels(), and the
        // unfiltered list made the toggle a no-op (every id "installed").
        // Machine-independent: on a clean machine the installed list is
        // empty and raw is the full flagged catalogue (~1770) — the count
        // separation below pins the regression everywhere.
        var service = new SherpaModelService(SpeechService.Instance);
        var installed = service.InstalledModels();
        Assert.All(installed, m => Assert.True(m.Installed));

        var raw = RustTtsWrapper.TtsClient.GetLocalModels(SherpaModelService.EngineId);
        Assert.True(raw.Count > installed.Count,
            $"raw list ({raw.Count}) should exceed the installed subset ({installed.Count})");
    }
}
