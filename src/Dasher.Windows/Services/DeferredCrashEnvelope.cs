using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Dasher.Windows.Services;

/// <summary>
/// #115: builds the PostHog error-tracking envelope ($exception_type /
/// $exception_message / $exception_list with structured frames) for a
/// DEFERRED crash report from the ORIGINAL exception's saved type + stack
/// text. posthog-dotnet 2.7.1's CaptureException derives all three from the
/// Exception INSTANCE (ExceptionPropertiesBuilder.Build overwrites whatever
/// properties are passed), so a reconstructed wrapper can only ever report
/// its own type — and the SDK's frames come from StackTrace(exception, true),
/// which yields nothing usable for a never-thrown exception. The raw path —
/// Capture(distinctId, "$exception", properties) — sends properties
/// untouched, so AnalyticsService.FlushPendingCrash builds them with this
/// helper instead. Frame dictionaries mirror the SDK's own shape
/// (ExceptionPropertiesBuilder.BuildStackFrameList): platform/lang/function/
/// module/filename/lineno — "function" is what populates PostHog's
/// $exception_functions. No abs_path: the saved stacks are PII-scrubbed and
/// only basenames are needed.
/// </summary>
public static class DeferredCrashEnvelope
{
    // "   at Full.Type.Method(Args) in C:\path\File.cs:line 42" — the file
    // clause is optional (native/async frames have no file). Aggressive
    // match on the signature so generic/nested names survive.
    private static readonly Regex FrameRegex = new(
        @"^\s*at\s+(?<sig>[^\s(]+(?:\([^)]*\))?)(?:\s+in\s+(?<file>.+):line\s+(?<line>\d+))?\s*$",
        RegexOptions.Compiled);

    // The SDK caps at 50 frames per exception — match it.
    private const int MaxFrames = 50;

    /// <summary>
    /// Parse saved .NET stack text into $exception_list frame dictionaries
    /// (top of stack first, same order the SDK emits). Unparseable lines are
    /// skipped; at most <see cref="MaxFrames"/> frames are returned.
    /// </summary>
    public static List<Dictionary<string, object>> ParseStackFrames(string? stackTrace)
    {
        var frames = new List<Dictionary<string, object>>();
        if (string.IsNullOrWhiteSpace(stackTrace)) return frames;

        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (frames.Count >= MaxFrames) break;

            var m = FrameRegex.Match(line);
            if (!m.Success) continue;

            // "Ns.Type.Method(Args)" → module "Ns.Type", function "Method".
            // Method names may carry generic arity ("Foo`1"); the last dot
            // before the argument list splits module from method.
            var sig = m.Groups["sig"].Value;
            var paren = sig.IndexOf('(');
            var fullMethod = paren > 0 ? sig[..paren] : sig;
            var lastDot = fullMethod.LastIndexOf('.');
            var module = lastDot > 0 ? fullMethod[..lastDot] : "";
            var function = lastDot >= 0 && lastDot < fullMethod.Length - 1
                ? fullMethod[(lastDot + 1)..] : fullMethod;

            var file = m.Groups["file"].Success ? m.Groups["file"].Value : null;
            var lineNo = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var ln) ? ln : 0;

            frames.Add(new Dictionary<string, object>
            {
                ["platform"] = "custom",
                ["lang"] = "dotnet",
                ["filename"] = file is not null ? Path.GetFileName(file) : "",
                ["function"] = function,
                ["module"] = module,
                ["lineno"] = lineNo,
                ["colno"] = 0,
            });
        }
        return frames;
    }

    /// <summary>
    /// Extract the ORIGINAL exception's message from the saved stack body's
    /// first line. ex.ToString() renders "Type: message" (with an optional
    /// " (0xHRESULT)" between type and colon). When the line can't be
    /// parsed (empty body, scrubber damage, header-only), fall back to
    /// "{type}: {top frame}" so PostHog's grouping/function columns still
    /// carry signal (the issue's minimal requirement).
    /// </summary>
    public static string ParseOriginalMessage(string exceptionType, string? stackTrace)
    {
        var fallbackTop = "";
        foreach (var f in ParseStackFrames(stackTrace))
        {
            var fn = $"{f.GetValueOrDefault("module", "")}.{f.GetValueOrDefault("function", "")}".Trim('.');
            if (fn.Length > 0) { fallbackTop = fn; break; }
        }
        var fallback = fallbackTop.Length > 0 ? $"{exceptionType}: {fallbackTop}" : exceptionType;

        if (string.IsNullOrWhiteSpace(stackTrace)) return fallback;

        var firstLine = stackTrace.Split('\n', 2)[0].TrimEnd('\r');
        if (!firstLine.StartsWith(exceptionType, StringComparison.Ordinal)) return fallback;

        var rest = firstLine[exceptionType.Length..];
        // Strip " (0x80004005)" — the HResult the CLR renders for e.g.
        // SEHException — so the value matches the live ex.Message grouping.
        if (rest.StartsWith(" (0x", StringComparison.Ordinal))
        {
            var close = rest.IndexOf(')');
            if (close >= 0) rest = rest[(close + 1)..];
        }
        rest = rest.TrimStart();
        if (rest.StartsWith(':')) rest = rest[1..];
        rest = rest.TrimStart();
        return rest.Length > 0 ? rest : fallback;
    }

    /// <summary>
    /// The full $exception_list entry for the deferred crash (single
    /// exception, mechanism/stacktrace shape mirrors the SDK's builder).
    /// </summary>
    public static List<Dictionary<string, object>> BuildExceptionList(
        string exceptionType, string value, string? stackTrace) =>
    [
        new Dictionary<string, object>
        {
            ["type"] = exceptionType,
            ["value"] = value,
            ["mechanism"] = new Dictionary<string, object>
            {
                ["type"] = "generic",
                ["handled"] = true,
                ["source"] = "",
                ["synthetic"] = false,
            },
            ["stacktrace"] = new Dictionary<string, object>
            {
                ["frames"] = ParseStackFrames(stackTrace),
                ["type"] = "raw",
            },
        },
    ];
}
