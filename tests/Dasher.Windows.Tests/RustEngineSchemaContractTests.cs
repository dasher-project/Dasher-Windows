using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

// Shares the rust_tts_wrapper.dll native library with SherpaCapabilityTests
// — the collection keeps the two classes off xunit's parallel lanes.
[Collection("rust-tts-native")]
/// <summary>
/// Pins the C# credential mapping to the ACTUAL Rust schema, loaded from
/// the native rust_tts_wrapper.dll via TtsClient.GetEngineSetups(). Unlike
/// RustCredentialMappingTests (which pins the C# side against literals),
/// a bindings upgrade that renames or removes a schema key fails HERE —
/// these tests enforce the contract, the others document our mapping.
/// Windows-only by nature (win-x64 natives); runs in the Windows CI.
/// </summary>
public class RustEngineSchemaContractTests
{
    private static List<(string Engine, HashSet<string> Keys)> Setups { get; } = Load();

    private static List<(string Engine, HashSet<string> Keys)> Load()
    {
        try
        {
            return RustTtsWrapper.TtsClient.GetEngineSetups()
                .Select(s => (s.Id, s.Fields?.Select(f => f.Key).ToHashSet() ?? []))
                .ToList();
        }
        catch (Exception ex)
        {
            // Make the failure mode loud but identifiable in CI logs.
            throw new InvalidOperationException(
                $"Could not load Rust engine setups — native DLL missing? {ex.Message}", ex);
        }
    }

    private static HashSet<string> SchemaKeys(string engine)
    {
        var found = Setups.FirstOrDefault(s => s.Engine == engine);
        Assert.True(found != default, $"engine '{engine}' missing from Rust GetEngineSetups()");
        return found.Keys;
    }

    [Fact]
    public void Every_offered_engine_exists_in_the_rust_backend()
    {
        var rustEngines = Setups.Select(s => s.Engine).ToHashSet();
        foreach (var engine in SpeechService.EngineNames)
            Assert.Contains(engine, rustEngines);
    }

    [Fact]
    public void Unsupported_local_engines_are_not_offered()
    {
        var offered = SpeechService.EngineNames.ToHashSet();
        Assert.DoesNotContain("qwen", offered);
        Assert.DoesNotContain("qwen3-local", offered);
        Assert.DoesNotContain("omnivoice-local", offered);
    }

    [Theory]
    [InlineData("sapi")]
    [InlineData("edge")]
    public void Credential_free_engines_have_empty_schemas(string engine)
    {
        Assert.Empty(SchemaKeys(engine));
    }

    public static TheoryData<string, string[]> SentKeys => new()
    {
        { "azure",   new[] { "subscriptionKey", "region" } },
        { "google",  new[] { "keyFilePath" } }, // or apiKey
        { "polly",   new[] { "accessKeyId", "secretAccessKey", "region" } },
        { "watson",  new[] { "apiKey", "region", "instanceId" } },
        { "playht",  new[] { "apiKey", "userId" } },
        { "witai",   new[] { "token" } },
        { "openai",  new[] { "apiKey" } },
    };

    [Theory]
    [MemberData(nameof(SentKeys))]
    public void Every_sent_key_exists_in_the_rust_schema(string engine, string[] sentKeys)
    {
        var schema = SchemaKeys(engine);
        foreach (var key in sentKeys)
            Assert.Contains(key, schema);
    }

    [Fact]
    public void Every_settings_field_maps_into_the_rust_schema()
    {
        // The UI must never ask for a key the engine wouldn't accept —
        // checked per-engine against the live schema, not literals.
        foreach (var engine in SpeechService.EngineNames)
        {
            var schema = SchemaKeys(engine);
            foreach (var stored in SpeechService.RequiredCredentialKeys(engine))
            {
                var sent = RustCredentialMappingTests.ExpectedRustKeysFor(engine, stored);
                Assert.Contains(sent, schema);
            }
        }
    }

    [Fact]
    public void Sherpa_optional_model_fields_exist_in_schema()
    {
        var schema = SchemaKeys("sherpaonnx");
        Assert.Contains("modelPath", schema);
        Assert.Contains("modelId", schema);
    }
}
