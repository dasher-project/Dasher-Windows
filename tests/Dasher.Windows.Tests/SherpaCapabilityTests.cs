using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

// NOTE: this class shares the rust_tts_wrapper.dll native library with
// RustEngineSchemaContractTests; both carry [Collection("rust-tts-native")]
// so xunit runs them sequentially instead of contending for the DLL
// (observed as an intermittent SearchModels failure in full-suite runs).

/// <summary>
/// Windows synthesis capability per sherpa model family (verified by live
/// probes at RustTtsWrapper.Bindings 1.13.2 — see AACTools/rust-tts-
/// wrapper#116): MMS, Kokoro, and VITS/Piper speak (VITS unlocked by the
/// 1.13.2 sherpa-g2p fix); Kitten still fails engine creation and Matcha
/// still fails tokenization. This gate keeps AAC users from downloading a
/// voice that cannot speak in this build.
/// </summary>
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
        var registry = RustTtsWrapper.TtsClient.SearchModels("", 400);
        Assert.True(registry.Count >= 300, $"expected the full catalogue, got {registry.Count}");
        foreach (var family in SherpaSynthesisCapability.SpeakableTypes)
        {
            Assert.Contains(registry, m => string.Equals(m.ModelType, family, StringComparison.OrdinalIgnoreCase));
        }
    }
}
