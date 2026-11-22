using Dasher.Windows.Speech;
using Xunit;

namespace Dasher.Windows.Tests;

/// <summary>
/// FriendlyProviderError: raw provider error blobs (Google's is a
/// multi-line JSON body) must reduce to the one human sentence, and
/// non-JSON errors truncate instead of flooding the settings panel.
/// </summary>
public class FriendlyProviderErrorTests
{
    [Fact]
    public void Google_json_body_reduces_to_its_message_sentence()
    {
        var raw = """
            API error 400 Bad Request: {
              "error": {
                "code": 400,
                "message": "Synthesis input contains unsupported SSML tag",
                "status": "INVALID_ARGUMENT"
              }
            }
            """;
        var friendly = SpeechService.FriendlyProviderError(raw);
        Assert.Equal("Synthesis input contains unsupported SSML tag", friendly);
        Assert.DoesNotContain("{", friendly);
    }

    [Fact]
    public void Top_level_message_field_is_used()
    {
        var raw = """{"message": "quota exceeded for today"}""";
        Assert.Equal("quota exceeded for today", SpeechService.FriendlyProviderError(raw));
    }

    [Fact]
    public void Non_json_errors_truncate_to_one_line()
    {
        var raw = new string('x', 400);
        var friendly = SpeechService.FriendlyProviderError(raw);
        Assert.True(friendly.Length <= 161);
        Assert.EndsWith("…", friendly);
    }

    [Fact]
    public void Short_non_json_errors_pass_through()
    {
        Assert.Equal("Connection refused", SpeechService.FriendlyProviderError("Connection refused"));
    }

    [Fact]
    public void Trailing_log_noise_after_json_does_not_break_parsing()
    {
        var raw = """HTTP 403: {"error":{"message":"key invalid"}} and then some retry noise""";
        Assert.Equal("key invalid", SpeechService.FriendlyProviderError(raw));
    }

    [Fact]
    public void Empty_is_safe()
    {
        Assert.Equal("Unknown error.", SpeechService.FriendlyProviderError(""));
    }
}
