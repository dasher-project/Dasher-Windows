using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

public class PiiScrubberTests
{
    [Fact]
    public void Empty_string_passes_through()
    {
        Assert.Equal("", PiiScrubber.Scrub(""));
    }

    [Fact]
    public void Null_passes_through()
    {
        Assert.Null(PiiScrubber.Scrub(null!));
    }

    [Fact]
    public void Clean_string_passes_through()
    {
        var input = "System.NullReferenceException at DasherCanvas.cs:123";
        Assert.Equal(input, PiiScrubber.Scrub(input));
    }

    [Fact]
    public void Windows_home_path_is_scrubbed()
    {
        var input = @"   at Dasher.Windows.MainWindow() in C:\Users\willwade\src\MainWindow.axaml.cs:line 42";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains(@"C:\Users\<user>", result);
        Assert.DoesNotContain("willwade", result);
    }

    [Fact]
    public void Windows_home_path_with_spaces_is_scrubbed()
    {
        var input = @"   at Foo() in C:\Users\Steve Saling\Documents\dasher.cs:line 1";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains(@"C:\Users\<user>", result);
        Assert.DoesNotContain("Steve Saling", result);
    }

    [Fact]
    public void Unix_home_path_is_scrubbed()
    {
        var input = "   at Foo() in /Users/jane/code/dasher.swift:1";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains("/Users/<user>", result);
        Assert.DoesNotContain("jane", result);
    }

    [Fact]
    public void Linux_home_path_is_scrubbed()
    {
        var input = "   at Foo() in /home/bob/dasher/main.cpp:1";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains("/home/<user>", result);
        Assert.DoesNotContain("bob", result);
    }

    [Fact]
    public void Email_is_scrubbed()
    {
        var input = "Contact: willwade@gmail.com for details";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains("<email>", result);
        Assert.DoesNotContain("willwade@gmail.com", result);
    }

    [Fact]
    public void Multiple_emails_are_scrubbed()
    {
        var input = "From: alice@test.com To: bob@example.org";
        var result = PiiScrubber.Scrub(input);
        Assert.DoesNotContain("alice@", result);
        Assert.DoesNotContain("bob@", result);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, "<email>").Count);
    }

    [Fact]
    public void Multiple_pii_types_scrubbed_together()
    {
        var input = @"Error at C:\Users\john\app.cs — contact john@doe.com";
        var result = PiiScrubber.Scrub(input);
        Assert.Contains(@"C:\Users\<user>", result);
        Assert.Contains("<email>", result);
        Assert.DoesNotContain("john", result);
        Assert.DoesNotContain("doe.com", result);
    }

    [Fact]
    public void Stack_trace_with_paths_is_scrubbed()
    {
        var input = """
            System.NullReferenceException: Object reference not set
               at Dasher.Windows.MainWindow.OnTogglePrefs() in C:\Users\willwade\src\MainWindow.axaml.cs:line 42
               at Dasher.Windows.App.OnUnhandledException() in C:\Users\willwade\src\App.axaml.cs:line 60
            """;
        var result = PiiScrubber.Scrub(input);
        Assert.DoesNotContain("willwade", result);
        Assert.Contains("MainWindow.axaml.cs", result);
        Assert.Contains("App.axaml.cs", result);
    }
}
