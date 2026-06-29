using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PostHog;
using PostHog.Sdk;

namespace Dasher.Windows.Services;

/// <summary>
/// Privacy-preserving analytics wrapper around PostHog.
/// All events are opt-in. No typed text, clipboard, or PII is ever sent.
/// Implements RFC 0009 crash reporting with engine log ring buffer.
/// </summary>
public static class AnalyticsService
{
    private const string ProjectToken = "phc_ubtNRuCT7Zqo4dVrVWRnJRYE9m9WqGeTyK7zVDKQ968J";

    /// <summary>
    /// Identifies which frontend this is. All Dasher frontends share the same
    /// PostHog project, so this distinguishes Windows / macOS / iOS / Linux events.
    /// </summary>
    private const string Platform = "windows";

    private static AnalyticsSettings _settings = new();
    private static string _distinctId = "";
    private static bool _initialized;

    // ── Engine log ring buffer (RFC 0009) ──────────────────────────────────────
    private static readonly EngineLogRingBuffer _engineLog = new();

    // ── Crash file (RFC 0009) ──────────────────────────────────────────────────
    private static readonly string CrashFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dasher", "pending_crash.txt");
    private static readonly TimeSpan CrashMaxAge = TimeSpan.FromDays(7);

    public static bool IsOptedIn => _settings.OptedIn;
    public static bool HasPrompted => _settings.PromptShown;
    public static string AnonymousId => _distinctId;

    public static void Initialize()
    {
        _settings = AnalyticsSettings.Load();
        _distinctId = _settings.GetOrCreateAnonymousId();

        if (_settings.OptedIn)
            StartPostHog();

        // Flush any pending crash from a previous session (RFC 0009)
        // Must run AFTER StartPostHog so the SDK is ready to send
        FlushPendingCrash();

        if (!_settings.OptedIn)
            return;
    }

    private static void StartPostHog()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            PostHogSdk.Init(new PostHogOptions
            {
                ProjectToken = ProjectToken,
                HostUrl = new Uri("https://eu.i.posthog.com"),
            });
        }
        catch { }
    }

    public static void SetOptIn(bool optedIn)
    {
        _settings.OptedIn = optedIn;
        _settings.PromptShown = true;
        _settings.Save();

        if (optedIn)
        {
            StartPostHog();
            Capture("analytics_opted_in");
        }
    }

    public static void ResetAnonymousId()
    {
        var wasOptedIn = _settings.OptedIn;
        if (wasOptedIn)
            Capture("analytics_id_reset");

        _settings.ResetAnonymousId();
        _distinctId = _settings.GetOrCreateAnonymousId();
    }

    /// <summary>
    /// Returns the default properties appended to every event so all frontends
    /// can be distinguished in the shared PostHog project.
    /// </summary>
    private static Dictionary<string, object> DefaultProperties()
    {
        return new Dictionary<string, object>
        {
            ["platform"] = Platform,
            ["app_variant"] = "dasher-windows",
            ["app_version"] = UpdateChecker.GetCurrentVersion(),
            ["os_version"] = RuntimeInformation.OSDescription,
        };
    }

    /// <summary>
    /// Capture an analytics event. No-ops if the user has not opted in.
    /// Platform, app_variant, app_version and os_version are auto-included.
    /// NEVER pass typed text, clipboard contents, or PII as properties.
    /// </summary>
    public static void Capture(string eventName, Dictionary<string, object>? properties = null)
    {
        if (!_settings.OptedIn || !_initialized) return;

        try
        {
            var props = DefaultProperties();
            if (properties != null)
            {
                foreach (var kv in properties)
                    props[kv.Key] = kv.Value;
            }
            PostHogSdk.Capture(_distinctId, eventName, props);
        }
        catch { }
    }

    // ── Engine log ring buffer (RFC 0009) ──────────────────────────────────────

    /// <summary>
    /// Append an engine log line to the ring buffer.
    /// Called from the dasher_set_log_callback dispatch in DasherCanvas.
    /// </summary>
    public static void AppendEngineLog(int level, string message)
    {
        _engineLog.Append(level, message);
    }

    private static string SnapshotEngineLog()
    {
        return _engineLog.Snapshot();
    }

    // ── Crash reporting (RFC 0009) ─────────────────────────────────────────────

    /// <summary>
    /// Write a crash file to disk. Called from the unhandled-exception handler.
    /// The file is flushed to PostHog on next launch if the user has opted in.
    /// </summary>
    public static void WriteCrashFile(Exception ex, string? source = null)
    {
        try
        {
            var stack = PiiScrubber.Scrub(ex.ToString());
            if (stack.Length > 16 * 1024) stack = stack[..(16 * 1024)];

            var engineTail = PiiScrubber.Scrub(SnapshotEngineLog());
            if (engineTail.Length > 8 * 1024) engineTail = engineTail[..(8 * 1024)];

            var sb = new StringBuilder();
            // Header (key=value lines)
            sb.Append("exception_type=").Append(ex.GetType().FullName).Append('\n');
            sb.Append("source=").Append(source ?? "AppDomain.UnhandledException").Append('\n');
            sb.Append("app_version=").Append(UpdateChecker.GetCurrentVersion()).Append('\n');
            sb.Append("os_version=").Append(RuntimeInformation.OSDescription).Append('\n');
            sb.Append("\n\n");
            // Body: stack trace + engine log tail
            sb.Append(stack);
            if (!string.IsNullOrWhiteSpace(engineTail))
                sb.Append("\n--- engine log ---\n").Append(engineTail);

            Directory.CreateDirectory(Path.GetDirectoryName(CrashFilePath)!);
            File.WriteAllText(CrashFilePath, sb.ToString());
        }
        catch { /* never throw in a crash handler */ }
    }

    /// <summary>
    /// On launch: if a pending crash file exists, send it (only if opted in)
    /// and delete it. Crash files older than 7 days are discarded.
    /// </summary>
    private static void FlushPendingCrash()
    {
        try
        {
            if (!File.Exists(CrashFilePath)) return;

            var age = DateTime.Now - File.GetLastWriteTime(CrashFilePath);
            if (age > CrashMaxAge)
            {
                File.Delete(CrashFilePath);
                return;
            }

            if (_settings.OptedIn && _initialized)
            {
                var content = File.ReadAllText(CrashFilePath);
                var splitIdx = content.IndexOf("\n\n");
                var header = splitIdx > 0 ? content[..splitIdx] : "";
                var body = splitIdx > 0 ? content[(splitIdx + 2)..] : content;

                // Parse header
                var crashProps = DefaultProperties();
                string exceptionType = "System.Exception";
                string source = "unknown";
                foreach (var line in header.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = line[..eq];
                        var val = line[(eq + 1)..];
                        if (key == "exception_type") exceptionType = val;
                        else if (key == "source") source = val;
                    }
                }

                // Split body into stack trace and engine log tail
                var bodyParts = body.Split("\n--- engine log ---", 2, StringSplitOptions.None);
                var stackTrace = bodyParts[0].Trim();
                if (!string.IsNullOrWhiteSpace(stackTrace))
                    crashProps["stack_trace"] = stackTrace;
                if (bodyParts.Length > 1 && !string.IsNullOrWhiteSpace(bodyParts[1]))
                    crashProps["engine_log_tail"] = bodyParts[1].Trim();
                crashProps["source"] = source;

                // Use CaptureException so PostHog Error Tracking sees it as $exception
                var ex = new SavedCrashException(exceptionType, stackTrace);
                PostHogSdk.CaptureException(ex, _distinctId, crashProps, null, false, DateTimeOffset.UtcNow);
                PostHogSdk.FlushAsync().Wait(TimeSpan.FromSeconds(5));
            }

            File.Delete(CrashFilePath);
        }
        catch { }
    }

    /// <summary>
    /// Capture a crash/exception directly to PostHog (for non-fatal exceptions).
    /// For fatal crashes, use WriteCrashFile instead.
    /// </summary>
    public static void CaptureCrash(Exception ex)
    {
        if (!_settings.OptedIn || !_initialized) return;

        try
        {
            PostHogSdk.CaptureException(ex, _distinctId, DefaultProperties(), null, false, DateTimeOffset.UtcNow);
        }
        catch { }
    }

    public static async Task FlushAsync()
    {
        if (!_initialized) return;
        try { await PostHogSdk.FlushAsync(); } catch { }
    }

    public static async Task ShutdownAsync()
    {
        if (!_initialized) return;
        try { await PostHogSdk.ShutdownAsync(); } catch { }
    }
}

/// <summary>
/// Reconstructed exception for deferred crash reporting (RFC 0009).
/// Carries the original exception type name and saved stack trace so
/// PostHogSdk.CaptureException produces a proper $exception event.
/// </summary>
internal sealed class SavedCrashException : Exception
{
    private readonly string _savedStackTrace;

    public SavedCrashException(string originalTypeName, string savedStackTrace)
        : base($"Deferred crash: {originalTypeName}")
    {
        _savedStackTrace = savedStackTrace;
        Source = originalTypeName;
    }

    public override string ToString()
    {
        return _savedStackTrace;
    }
}
