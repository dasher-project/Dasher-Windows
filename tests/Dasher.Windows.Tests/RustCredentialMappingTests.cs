using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Pins the stored-PascalCase → Rust-camelCase credential mapping of
/// BuildRustCredentials (the C# side of the contract). RustEngineSchema-
/// ContractTests enforces the Rust side against the native DLL; together
/// a bindings upgrade that renames a key fails a test instead of a user's
/// speech silently breaking.
/// </summary>
public class RustCredentialMappingTests
{
    private static Dictionary<string, string> BuildFor(string engine, Dictionary<string, string> creds) =>
        SpeechService.BuildRustCredentials(engine, creds);

    /// <summary>Stored PascalCase key → the Rust camelCase key we send for
    /// it. Shared with RustEngineSchemaContractTests so the live-schema
    /// check doesn't drift from this mapping.</summary>
    public static string ExpectedRustKeysFor(string engine, string storedKey) => (engine, storedKey) switch
    {
        ("azure", "SubscriptionKey") => "subscriptionKey",
        ("azure", "Region") => "region",
        ("google", "KeyFilePath") => "keyFilePath",
        ("google", "ApiKey") => "apiKey",
        ("polly", "AccessKeyId") => "accessKeyId",
        ("polly", "SecretAccessKey") => "secretAccessKey",
        ("polly", "Region") => "region",
        ("watson", "ApiKey") => "apiKey",
        ("watson", "Region") => "region",
        ("watson", "InstanceId") => "instanceId",
        ("playht", "ApiKey") => "apiKey",
        ("playht", "UserId") => "userId",
        ("witai", "Token") => "token",
        _ => "apiKey", // every remaining engine+field is a plain apiKey
    };

    [Theory]
    [InlineData("sapi")]
    [InlineData("edge")]
    public void Credential_free_engines_send_nothing(string engine)
    {
        var c = BuildFor(engine, new Dictionary<string, string> { ["ApiKey"] = "x" });
        Assert.Empty(c);
    }

    public static TheoryData<string, string[], string[]> EngineSchemas => new()
    {
        // engine, stored PascalCase keys, expected Rust camelCase keys (all filled)
        { "azure",    new[] { "SubscriptionKey", "Region" },                new[] { "subscriptionKey", "region" } },
        { "polly",    new[] { "AccessKeyId", "SecretAccessKey", "Region" }, new[] { "accessKeyId", "secretAccessKey", "region" } },
        { "watson",   new[] { "ApiKey", "Region", "InstanceId" },           new[] { "apiKey", "region", "instanceId" } },
        { "playht",   new[] { "ApiKey", "UserId" },                         new[] { "apiKey", "userId" } },
        { "witai",    new[] { "Token" },                                    new[] { "token" } },
        { "openai",   new[] { "ApiKey" },                                   new[] { "apiKey" } },
        { "gemini",   new[] { "ApiKey" },                                   new[] { "apiKey" } },
        { "cartesia", new[] { "ApiKey" },                                   new[] { "apiKey" } },
    };

    [Theory]
    [MemberData(nameof(EngineSchemas))]
    public void Filled_fields_map_to_rust_keys(string engine, string[] stored, string[] expected)
    {
        var creds = stored.ToDictionary(k => k, _ => "value");
        var c = BuildFor(engine, creds);
        Assert.Equal(expected.OrderBy(k => k), c.Keys.OrderBy(k => k));
    }

    [Theory]
    [MemberData(nameof(EngineSchemas))]
    public void Blank_fields_are_omitted(string engine, string[] stored, string[] _)
    {
        // Blank strings must not be sent: Rust treats a present key as set,
        // so "" would shadow engine defaults (e.g. watson's us-south region).
        var creds = stored.ToDictionary(k => k, _ => "  ");
        var c = BuildFor(engine, creds);
        Assert.Empty(c);
    }

    [Fact]
    public void Missing_fields_are_omitted()
    {
        // Genuinely absent keys (never typed, never LostFocus-saved) — the
        // dictionary simply doesn't contain them.
        var c = BuildFor("azure", new Dictionary<string, string>());
        Assert.Empty(c);

        var partial = BuildFor("watson", new Dictionary<string, string> { ["ApiKey"] = "k" });
        Assert.Equal(["apiKey"], partial.Keys);
    }

    [Fact]
    public void Simple_apiKey_engines_all_map()
    {
        foreach (var engine in SpeechService.EngineNames)
        {
            var c = BuildFor(engine, new Dictionary<string, string> { ["ApiKey"] = "k" });
            switch (engine)
            {
                case "sapi":
                case "edge":
                    Assert.Empty(c);
                    break;
                case "azure":
                case "polly":
                case "watson":
                case "playht":
                case "witai":
                case "google":
                case "sherpaonnx":
                    break; // per-engine cases covered elsewhere
                default:
                    Assert.Equal(["apiKey"], c.Keys);
                    break;
            }
        }
    }

    [Fact]
    public void Google_prefers_keyFilePath_and_falls_back_to_apiKey()
    {
        var both = BuildFor("google", new Dictionary<string, string>
        {
            ["KeyFilePath"] = "C:\\creds.json", ["ApiKey"] = "k"
        });
        Assert.Equal(["keyFilePath"], both.Keys);

        var apiKeyOnly = BuildFor("google", new Dictionary<string, string>
        {
            ["KeyFilePath"] = "", ["ApiKey"] = "k" // empty path must not block the fallback
        });
        Assert.Equal(["apiKey"], apiKeyOnly.Keys);
    }

    [Fact]
    public void Witai_legacy_ApiKey_still_works()
    {
        // Pre-Rust settings stored the wit.ai credential under ApiKey.
        var c = BuildFor("witai", new Dictionary<string, string> { ["ApiKey"] = "legacy" });
        Assert.Equal("legacy", c["token"]);
    }

    [Fact]
    public void Sherpa_passes_through_stored_model_fields()
    {
        var c = BuildFor("sherpaonnx", new Dictionary<string, string>
        {
            ["ModelPath"] = "C:\\models\\aacad69.onnx", ["ModelId"] = "aacad69"
        });
        Assert.Equal(2, c.Count);
        Assert.Equal("C:\\models\\aacad69.onnx", c["modelPath"]);
        Assert.Equal("aacad69", c["modelId"]);

        // No stored model → send nothing (engine defaults apply).
        Assert.Empty(BuildFor("sherpaonnx", []));
    }

    [Fact]
    public void Watson_legacy_service_url_migrates_to_region_and_instance()
    {
        var c = BuildFor("watson", new Dictionary<string, string>
        {
            ["ApiKey"] = "k",
            ["ServiceUrl"] = "https://api.eu-de.text-to-speech.watson.cloud.ibm.com/instances/1a2b3c4d-5e6f-7890-abcd-ef1234567890"
        });
        Assert.Equal("eu-de", c["region"]);
        Assert.Equal("1a2b3c4d-5e6f-7890-abcd-ef1234567890", c["instanceId"]);
    }

    [Fact]
    public void Watson_explicit_fields_win_over_legacy_service_url()
    {
        var c = BuildFor("watson", new Dictionary<string, string>
        {
            ["Region"] = "us-east",
            ["ServiceUrl"] = "https://api.eu-de.text-to-speech.watson.cloud.ibm.com/instances/1a2b3c4d-5e6f-7890-abcd-ef1234567890"
        });
        Assert.Equal("us-east", c["region"]);
    }

    [Theory]
    [InlineData("https://api.text-to-speech.watson.cloud.ibm.com/instances/x")]     // no region segment
    [InlineData("https://example.com/instances/1a2b3c4d")]                           // not watson
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Unparseable_service_urls_are_ignored(string url)
    {
        Assert.False(SpeechService.TryParseWatsonServiceUrl(url, out _, out _));
        var c = BuildFor("watson", new Dictionary<string, string> { ["ServiceUrl"] = url });
        Assert.False(c.ContainsKey("region"));
        Assert.False(c.ContainsKey("instanceId"));
    }

    [Fact]
    public void Settings_fields_can_supply_every_sent_key()
    {
        // RequiredCredentialKeys is what the settings UI renders; every key
        // BuildRustCredentials can send must be offerable to the user.
        foreach (var engine in SpeechService.EngineNames)
        {
            var offered = SpeechService.RequiredCredentialKeys(engine);
            if (offered.Length == 0) continue; // credential-free engine
            var creds = offered.ToDictionary(k => k, _ => "value");
            var c = BuildFor(engine, creds);
            Assert.NotEmpty(c);
        }
    }
}
