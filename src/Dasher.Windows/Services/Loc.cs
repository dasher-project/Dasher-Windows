using System;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;
using Dasher.Windows.Engine;

namespace Dasher.Windows.Services;

/// <summary>
/// Frontend UI string lookup (RFC 0003). Resolves keys from the shared
/// catalogue's .resx resources — .NET picks the satellite assembly that
/// matches CurrentUICulture, which follows the OS language (including
/// Windows' per-app language override), with automatic fallback to English.
///
/// At startup the system locale is also pushed into the engine via
/// dasher_set_locale so parameter labels localise from the same choice
/// (RFC 0003 "one locale" principle). No in-app picker: the system
/// language is the single source of truth.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager =
        new("Dasher.Windows.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Locales the shared catalogue ships translations for.</summary>
    private static readonly string[] CatalogueLocales =
    {
        "af", "ar", "bn", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr",
        "gu", "hi", "hu", "it", "kn", "ml", "mr", "nl", "pa", "pl", "pt",
        "pt-PT", "ru", "sv", "sw", "ta", "te", "th", "ur", "zh-CN", "zu",
    };

    private static string _locale = "en";

    public static string Current => _locale;

    /// <summary>The only RTL locales in the DasherCore catalogue (ar/fa/ur).</summary>
    public static bool IsRtl => _locale is "ar" or "fa" or "ur";

    /// <summary>
    /// Translate a catalogue key. Falls back to the supplied English default
    /// (or the key) when the culture or key has no translation.
    /// </summary>
    public static string Tr(string key, string? fallback = null)
    {
        try
        {
            var value = Manager.GetString(key, CultureInfo.CurrentUICulture);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        catch (MissingManifestResourceException) { }
        catch (CultureNotFoundException) { }
        return fallback ?? key;
    }

    /// <summary>
    /// Resolve the system UI culture to the nearest catalogue locale, apply it
    /// to the engine, and report RTL. Call once at startup, after the engine
    /// handle exists.
    /// </summary>
    public static void InitializeFromSystem(IntPtr engineHandle)
    {
        // Resolve against the ORIGINAL UI culture — the ResourceManager uses
        // it verbatim, so the engine must match it, not a reduced form.
        var code = ResolveCatalogueLocale(CultureInfo.CurrentUICulture);

        _locale = code;

        if (engineHandle != IntPtr.Zero)
        {
            try { NativeBridge.dasher_set_locale(engineHandle, code); }
            catch { }
        }
    }

    /// <summary>
    /// Map a culture to a catalogue locale: exact specific-culture matches win
    /// first (zh-CN and pt-PT are catalogue entries in their own right, and the
    /// ResourceManager will pick their satellite assemblies verbatim), then the
    /// parent chain narrows (en-GB → en, fr-CA → fr). English otherwise.
    /// </summary>
    public static string ResolveCatalogueLocale(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c?.Name); c = c.Parent)
        {
            if (CatalogueLocales.Contains(c.Name))
                return c.Name;
        }
        return "en";
    }
}
