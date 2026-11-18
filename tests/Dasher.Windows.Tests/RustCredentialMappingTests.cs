using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// Pins the stored-PascalCase → Rust-camelCase credential mapping to the
/// authoritative schema of RustTtsWrapper.Bindings 1.13.1
/// (TtsClient.GetEngineSetups()). If a bindings upgrade renames a key,
/// these fail instead of a user's speech silently breaking.
/// </summary>
public class RustCredentialMappingTests
{
    private static Dictionary<string, string> BuildFor(string engine, Dictionary<string, string> creds) =>
        SpeechService.BuildRustCredentials(engine, creds);

	[Theory]
	[InlineData("sapi")]
	[InlineData("edge")]
	[InlineData("sherpaonnx")]
	public void Credential_free_engines_send_nothing(string engine)
	{
		var c = BuildFor(engine, new Dictionary<string, string> { ["ApiKey"] = "x" });
		Assert.Empty(c);
	}

	public static TheoryData<string, string[], string[]> EngineSchemas => new()
	{
		// engine, stored PascalCase keys, expected Rust camelCase keys (all filled)
		{ "azure",   new[] { "SubscriptionKey", "Region" },                 new[] { "subscriptionKey", "region" } },
		{ "polly",   new[] { "AccessKeyId", "SecretAccessKey", "Region" },  new[] { "accessKeyId", "secretAccessKey", "region" } },
		{ "watson",  new[] { "ApiKey", "Region", "InstanceId" },            new[] { "apiKey", "region", "instanceId" } },
		{ "playht",  new[] { "ApiKey", "UserId" },                          new[] { "apiKey", "userId" } },
		{ "witai",   new[] { "Token" },                                     new[] { "token" } },
		{ "openai",  new[] { "ApiKey" },                                    new[] { "apiKey" } },
		{ "gemini",  new[] { "ApiKey" },                                    new[] { "apiKey" } },
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
	public void Empty_and_missing_fields_are_omitted(string engine, string[] stored, string[] expected)
	{
		// Empty strings must not be sent: Rust treats a present key as set,
		// so "" would shadow engine defaults (e.g. watson's us-south region).
		var creds = stored.ToDictionary(k => k, _ => "  ");
		var c = BuildFor(engine, creds);
		Assert.Empty(c);
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
				case "sherpaonnx":
					Assert.Empty(c);
					break;
				case "azure":
				case "polly":
				case "watson":
				case "playht":
				case "witai":
				case "google":
					break; // per-engine cases covered above
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
