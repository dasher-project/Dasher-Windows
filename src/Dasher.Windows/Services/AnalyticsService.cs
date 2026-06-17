using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PostHog;
using PostHog.Sdk;

namespace Dasher.Windows.Services;

/// <summary>
/// Privacy-preserving analytics wrapper around PostHog.
/// All events are opt-in. No typed text, clipboard, or PII is ever sent.
/// </summary>
public static class AnalyticsService
{
    private const string ProjectToken = "phc_ubtNRuCT7Zqo4dVrVWRnJRYE9m9WqGeTyK7zVDKQ968J";

    private static AnalyticsSettings _settings = new();
    private static string _distinctId = "";
    private static bool _initialized;

    public static bool IsOptedIn => _settings.OptedIn;
    public static bool HasPrompted => _settings.PromptShown;
    public static string AnonymousId => _distinctId;

    public static void Initialize()
    {
        _settings = AnalyticsSettings.Load();
        _distinctId = _settings.GetOrCreateAnonymousId();

        if (!_settings.OptedIn)
            return;

        StartPostHog();
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
    /// Capture an analytics event. No-ops if the user has not opted in.
    /// NEVER pass typed text, clipboard contents, or PII as properties.
    /// </summary>
    public static void Capture(string eventName, Dictionary<string, object>? properties = null)
    {
        if (!_settings.OptedIn || !_initialized) return;

        try
        {
            PostHogSdk.Capture(_distinctId, eventName, properties);
        }
        catch { }
    }

    /// <summary>
    /// Capture a crash/exception. Called from unhandled exception handlers.
    /// </summary>
    public static void CaptureCrash(Exception ex)
    {
        if (!_settings.OptedIn || !_initialized) return;

        try
        {
            PostHogSdk.CaptureException(ex, _distinctId, new Dictionary<string, object>
            {
                ["app_version"] = UpdateChecker.GetCurrentVersion(),
                ["os_version"] = RuntimeInformation.OSDescription,
            }, null, false, DateTimeOffset.UtcNow);
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
