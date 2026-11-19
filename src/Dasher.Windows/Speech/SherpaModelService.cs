using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dasher.Windows.Speech;

/// <summary>
/// Wraps the Rust bindings' Sherpa model-management API (registry search,
/// install with progress, uninstall) and applies Dasher's selection model:
/// the selected model is stored as the ModelId credential and consumed by
/// BuildRustCredentials when the sherpaonnx engine builds its client.
/// Mirrors Dasher-Apple's SpeechService sherpa plumbing.
/// </summary>
public sealed class SherpaModelService
{
    public const string EngineId = "sherpaonnx";

    private readonly SpeechService _speech;

    public SherpaModelService(SpeechService speech) => _speech = speech;

    public string? CurrentModelId =>
        _speech.Credentials.TryGetValue("ModelId", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;    /// <summary>Registry entries for a query (the wrapper's embedded
    /// catalogue — 1770 models at Bindings 1.13.2). Empty query lists the
    /// whole catalogue; the default limit of 5000 keeps generous headroom
    /// over the catalogue size (a tight cap is a silent truncation cliff —
    /// at limit 400 only 1 of 4 kokoro models appeared).</summary>
    public List<RustTtsWrapper.TtsLocalModel> Search(string query, int limit = 5000) =>
        RustTtsWrapper.TtsClient.SearchModels(query, limit);

    /// <summary>Locally installed models (under
    /// %USERPROFILE%\.rust-tts-wrapper\sherpaonnx by default). NOTE: the
    /// bindings' GetLocalModels returns the full catalogue (~1700 entries)
    /// carrying Installed flags, so filter to Installed=true — this is the
    /// set the browser's "Installed only" toggle is built from, and using
    /// the raw list made the filter a no-op.</summary>
    public List<RustTtsWrapper.TtsLocalModel> InstalledModels() =>
        RustTtsWrapper.TtsClient.GetLocalModels(EngineId)
            .Where(m => m.Installed)
            .ToList();

    public Task InstallAsync(string modelId, IProgress<(double Fraction, string Status)> progress, CancellationToken ct) =>
        RustTtsWrapper.TtsClient.InstallModelAsync(EngineId, modelId, progress, ct);

    public void Uninstall(string modelId) => RustTtsWrapper.TtsClient.UninstallModel(EngineId, modelId);

    /// <summary>Makes the model the sherpa engine's voice source: stored as
    /// the ModelId credential, client invalidated, voice selection reset
    /// (the new model's speaker list replaces it), settings saved.</summary>
    public void Select(string modelId)
    {
        _speech.Credentials["ModelId"] = modelId;
        _speech.SelectedVoiceId = null;
        _speech.InvalidateClient();
        _speech.SaveSettings();
    }
}

/// <summary>
/// Which sherpa model families can actually SPEAK in this build. Windows
/// capability is family-dependent (upstream issue AACTools/rust-tts-wrapper
/// #116), verified by live probes on win-x64 at Bindings 1.13.6: MMS,
/// Kokoro, and VITS/Piper since the 1.13.2 sherpa-g2p fix; Kitten since
/// the 1.13.6 lexicon-cache provisioning fix (bundles land in
/// %USERPROFILE%\.voicegarden-lexicons, which the wrapper manages — never
/// ship or hand-seed that directory in releases). Matcha is still gated:
/// its vocoder provisioning remains broken at 1.13.6 (fresh installs end
/// missing=[vocoder] and synthesis fails tokenization). The browser uses
/// this gate so AAC users can't install a voice that cannot speak.
/// </summary>
public static class SherpaSynthesisCapability
{
    /// <summary>Model types whose synthesis is verified on Windows.</summary>
    public static readonly HashSet<string> SpeakableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "mms",
        "kokoro",
        "vits",
        "kitten",
    };

    public static bool IsSpeakable(string? modelType) =>
        !string.IsNullOrEmpty(modelType) && SpeakableTypes.Contains(modelType);
}
