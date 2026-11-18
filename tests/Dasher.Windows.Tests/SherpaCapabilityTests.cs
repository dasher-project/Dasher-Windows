using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Windows synthesis capability per sherpa model family (verified by live
/// probes at RustTtsWrapper 1.13.1 — see AACTools/rust-tts-wrapper#116):
/// MMS and Kokoro speak; vits/piper and matcha fail tokenization; kitten
/// fails engine creation. This gate keeps AAC users from downloading a
/// voice that cannot speak in this build.
/// </summary>
public class SherpaCapabilityTests
{
    [Theory]
    [InlineData("mms")]
    [InlineData("kokoro")]
    [InlineData("MMS")]
    [InlineData("Kokoro")]
    public void Verified_families_are_speakable(string family)
    {
        Assert.True(SherpaSynthesisCapability.IsSpeakable(family));
    }

    [Theory]
    [InlineData("vits")]
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
