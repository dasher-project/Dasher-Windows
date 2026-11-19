using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using DotNetTtsWrapper.Models;

namespace Dasher.Windows.Speech;

public sealed class SpeechService : IDisposable
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dasher");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "tts_settings.json");

    public string SelectedEngine { get; set; } = "sapi";
    public Dictionary<string, string> Credentials { get; set; } = new();
    public string? SelectedVoiceId { get; set; }
    /// <summary>Sherpa's voice source is the selected model: true when a
    /// usable ModelId credential is stored. Shared by the no-model error
    /// message, the settings Retry/Browse visibility, and the model
    /// service — keep one definition.</summary>
    public bool HasSherpaModel =>
        SelectedEngine == "sherpaonnx" &&
        Credentials.TryGetValue("ModelId", out var mid) && !string.IsNullOrWhiteSpace(mid);
    public List<TtsVoice> AvailableVoices { get; private set; } = [];
    public bool IsSpeaking { get; private set; }
    public bool IsLoadingVoices { get; private set; }
    public string? ErrorMessage { get; private set; }
    public SpeechRate SpeechRate { get; set; } = SpeechRate.Medium;
    public SpeechPitch SpeechPitch { get; set; } = SpeechPitch.Medium;
    public int SpeechVolume { get; set; } = 80;

    public static readonly string[] EngineNames =
    [
        "sapi", "edge", "azure", "google", "polly", "openai", "elevenlabs",
        "watson", "playht", "witai", "gemini", "cartesia", "deepgram",
        "hume", "xai", "fishaudio", "mistral", "murf", "unrealspeech",
        "resemble", "upliftai", "modelslab", "sherpaonnx"
    ];

    public static string EngineDisplayName(string engine) => engine switch
    {
        "sapi" => "SAPI (Windows)",
        "edge" => "Microsoft Edge (Read Aloud)",
        "azure" => "Azure Cognitive Services",
        "google" => "Google Cloud TTS",
        "polly" => "Amazon Polly",
        "openai" => "OpenAI TTS",
        "elevenlabs" => "ElevenLabs",
        "watson" => "IBM Watson",
        "playht" => "PlayHT",
        "witai" => "Wit.ai",
        "gemini" => "Google Gemini",
        "cartesia" => "Cartesia",
        "deepgram" => "Deepgram",
        "hume" => "Hume AI",
        "xai" => "xAI Grok",
        "fishaudio" => "Fish Audio",
        "mistral" => "Mistral AI",
        "murf" => "Murf AI",
        "unrealspeech" => "Unreal Speech",
        "resemble" => "Resemble AI",
        "upliftai" => "Uplift AI",
        "modelslab" => "ModelsLab",
        "sherpaonnx" => "SherpaONNX (Local)",
        _ => engine
    };

    public static string[] RequiredCredentialKeys(string engine) => engine switch
    {
        "azure" => ["SubscriptionKey", "Region"],
        "google" => ["KeyFilePath", "ApiKey"],
        "polly" => ["AccessKeyId", "SecretAccessKey", "Region"],
        "openai" => ["ApiKey"],
        "elevenlabs" => ["ApiKey"],
        "watson" => ["ApiKey", "Region", "InstanceId"],
        "playht" => ["ApiKey", "UserId"],
        "witai" => ["Token"],
        "gemini" => ["ApiKey"],
        "cartesia" => ["ApiKey"],
        "deepgram" => ["ApiKey"],
        "hume" => ["ApiKey"],
        "xai" => ["ApiKey"],
        "fishaudio" => ["ApiKey"],
        "mistral" => ["ApiKey"],
        "murf" => ["ApiKey"],
        "unrealspeech" => ["ApiKey"],
        "resemble" => ["ApiKey"],
        "upliftai" => ["ApiKey"],
        "modelslab" => ["ApiKey"],
        _ => []
    };

    private static readonly Lazy<SpeechService> _instance = new(() => new SpeechService());
    public static SpeechService Instance => _instance.Value;

    private AbstractTtsClient? _client;
    private bool _needsRecreate = true;
    private SpeechSynthesizer? _sapiSynth;

    private SpeechService()
    {
        LoadSettings();
    }

    public async Task LoadVoicesAsync()
    {
        IsLoadingVoices = true;
        ErrorMessage = null;
        try
        {
            if (SelectedEngine == "sapi")
            {
                _sapiSynth?.Dispose();
                _sapiSynth = new SpeechSynthesizer();
                var installed = _sapiSynth.GetInstalledVoices();
                AvailableVoices = installed
                    .Where(v => v.Enabled)
                    .Select(v => new TtsVoice
                    {
                        Id = v.VoiceInfo.Name,
                        Name = v.VoiceInfo.Name,
                        Gender = v.VoiceInfo.Gender switch
                        {
                            System.Speech.Synthesis.VoiceGender.Male => DotNetTtsWrapper.Models.VoiceGender.Male,
                            System.Speech.Synthesis.VoiceGender.Female => DotNetTtsWrapper.Models.VoiceGender.Female,
                            _ => DotNetTtsWrapper.Models.VoiceGender.Unknown
                        },
                        LanguageCodes =
                        [
                            new() { Bcp47 = v.VoiceInfo.Culture?.Name ?? "", Display = v.VoiceInfo.Culture?.DisplayName ?? "" }
                        ]
                    })
                    .ToList();
            }
            else
            {
                var client = GetOrCreateClient();
                if (client == null)
                {
                    AvailableVoices = [];
                    return;
                }
                AvailableVoices = await client.GetVoicesAsync();
                // Sherpa lists 0 voices SILENTLY when no model is selected
                // (the model is the voice source). Make that actionable.
                if (SelectedEngine == "sherpaonnx" && AvailableVoices.Count == 0 && !HasSherpaModel)
                {
                    ErrorMessage = "No voice model selected — use Browse… to download one.";
                }
            }
            if (SelectedVoiceId != null && !AvailableVoices.Any(v => v.Id == SelectedVoiceId))
                SelectedVoiceId = AvailableVoices.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            AvailableVoices = [];
        }
        finally
        {
            IsLoadingVoices = false;
        }
    }

    public async Task SpeakAsync(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        IsSpeaking = true;
        ErrorMessage = null;
        try
        {
            if (SelectedEngine == "sapi")
            {
                await SpeakSapiAsync(text, interrupt);
                return;
            }

            var client = GetOrCreateClient();
            if (client == null) return;
            if (!string.IsNullOrEmpty(SelectedVoiceId))
                client.SetVoice(SelectedVoiceId);
            var options = new TtsOptions
            {
                Rate = SpeechRate,
                Pitch = SpeechPitch,
                Volume = SpeechVolume
            };
            if (interrupt)
                client.Stop();
            // Native playback for all Rust engines: since Bindings 1.13.5
            // the Windows sherpa path plays in-process via WASAPI — no
            // powershell subprocess, no %TEMP% wav, and TtsOptions volume
            // is pre-applied to the samples (verified live on 1.13.5:
            // zero spawns, no temp file, volume 100 vs 20 clearly
            // distinct). Our SynthToBytesAsync workaround for the old
            // powershell player was retired with that release; see
            // AACTools/rust-tts-wrapper#120.
            await client.SpeakAsync(text, options);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    private async Task SpeakSapiAsync(string text, bool interrupt)
    {
        var synth = _sapiSynth ??= new SpeechSynthesizer();

        if (interrupt)
            synth.SpeakAsyncCancelAll();

        // Select voice
        if (!string.IsNullOrEmpty(SelectedVoiceId))
        {
            try { synth.SelectVoice(SelectedVoiceId); } catch { }
        }

        // Build SSML with ALL prosody attributes in one element.
        // When using SpeakSsml(), the synthesizer's native Rate/Volume
        // properties are IGNORED — only the SSML prosody values matter.
        // So we must include rate, pitch, AND volume here.
        var rateStr = SpeechRate switch
        {
            SpeechRate.XSlow => "x-slow",
            SpeechRate.Slow => "slow",
            SpeechRate.Medium => "medium",
            SpeechRate.Fast => "fast",
            SpeechRate.XFast => "x-fast",
            _ => "medium"
        };

        var pitchStr = SpeechPitch switch
        {
            SpeechPitch.XLow => "x-low",
            SpeechPitch.Low => "low",
            SpeechPitch.Medium => "medium",
            SpeechPitch.High => "high",
            SpeechPitch.XHigh => "x-high",
            _ => "medium"
        };

        var volumeStr = Math.Clamp(SpeechVolume, 0, 100).ToString();

        var escapedText = XmlEncode(text);
        var culture = "en-US";
        try { culture = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Name == SelectedVoiceId)?.VoiceInfo.Culture?.Name ?? "en-US"; } catch { }

        var ssml = $"<speak version=\"1.0\" xml:lang=\"{culture}\">" +
                   $"<prosody rate=\"{rateStr}\" pitch=\"{pitchStr}\" volume=\"{volumeStr}\">{escapedText}</prosody>" +
                   $"</speak>";

        synth.SetOutputToDefaultAudioDevice();
        await Task.Run(() => synth.SpeakSsml(ssml));
    }

    private static string XmlEncode(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 20);
        foreach (var c in text)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public void Stop()
    {
        try
        {
            _sapiSynth?.SpeakAsyncCancelAll();
            _client?.Stop();
        }
        catch { }
        IsSpeaking = false;
    }

    public void InvalidateClient()
    {
        _needsRecreate = true;
    }

    private AbstractTtsClient? GetOrCreateClient()
    {
        if (!_needsRecreate && _client != null) return _client;
        var old = _client;
        try
        {
            // Rust TTS backend (parity with Dasher-Apple): same
            // AbstractTtsClient surface, Rust engine underneath. The
            // native DLLs ship with the NuGet package (win-x64).
            var rustCreds = BuildRustCredentials(SelectedEngine, Credentials);
            _client = new RustTtsWrapper.RustTtsClient(SelectedEngine, rustCreds);
            _needsRecreate = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _client = null;
            return null;
        }
        finally
        {
            // Dispose off the UI thread: the Rust dispose path stops and
            // joins in-flight synthesis (up to a 10s wait per request), and
            // a stalled network synth must not freeze the settings panel.
            if (old != null)
                _ = Task.Run(() => { try { old.Dispose(); } catch { } });
        }
        return _client;
    }

    /// <summary>
    /// Maps stored PascalCase credential keys to the Rust wrapper's canonical
    /// camelCase keys. Schema source of truth: TtsClient.GetEngineSetups()
    /// (verified against RustTtsWrapper.Bindings 1.13.1; pinned by
    /// RustCredentialMappingTests). Only non-empty values are sent: the Rust
    /// engines read credentials with a present-key check, so an empty string
    /// would shadow an engine default (e.g. watson's us-south region).
    /// Engines not listed per-engine below take a plain apiKey;
    /// sapi/edge/sherpaonnx need none.
    /// </summary>
    internal static Dictionary<string, string> BuildRustCredentials(
        string engine, IReadOnlyDictionary<string, string> credentials)
    {
        string? Val(string key) => credentials.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        var c = new Dictionary<string, string>();
        switch (engine)
        {
            case "sapi":
            case "edge":
                break; // no credentials
            case "sherpaonnx":
                // Optional model selection fields (schema: modelPath, modelId,
                // numThreads, numSteps, provider, referenceAudio, referenceText).
                // Historically we stored ModelPath/ModelId; keep passing them
                // through so users with a custom model keep working.
                if (Val("ModelPath") is { } modelPath) c["modelPath"] = modelPath;
                if (Val("ModelId") is { } modelId) c["modelId"] = modelId;
                break;
            case "azure":
                if (Val("SubscriptionKey") is { } subKey) c["subscriptionKey"] = subKey;
                if (Val("Region") is { } azRegion) c["region"] = azRegion;
                break;
            case "google":
                // Rust accepts either keyFilePath or apiKey (RequiredAny).
                if (Val("KeyFilePath") is { } kfp) c["keyFilePath"] = kfp;
                else if (Val("ApiKey") is { } gk) c["apiKey"] = gk;
                break;
            case "polly":
                if (Val("AccessKeyId") is { } accessKey) c["accessKeyId"] = accessKey;
                if (Val("SecretAccessKey") is { } secret) c["secretAccessKey"] = secret;
                if (Val("Region") is { } pollyRegion) c["region"] = pollyRegion;
                break;
            case "watson":
                if (Val("ApiKey") is { } watsonKey) c["apiKey"] = watsonKey;
                if (Val("Region") is { } watsonRegion) c["region"] = watsonRegion;
                if (Val("InstanceId") is { } inst) c["instanceId"] = inst;
                // Legacy migration: pre-Rust settings stored a ServiceUrl of
                // the form https://api.<region>.text-to-speech.watson.cloud.ibm.com/instances/<id>
                // which encodes both region and instanceId.
                if (Val("ServiceUrl") is { } svcUrl && TryParseWatsonServiceUrl(svcUrl, out var r, out var i))
                {
                    c.TryAdd("region", r);
                    c.TryAdd("instanceId", i);
                }
                break;
            case "playht":
                if (Val("ApiKey") is { } playKey) c["apiKey"] = playKey;
                if (Val("UserId") is { } userId) c["userId"] = userId;
                break;
            case "witai":
                // "Token" is the current field; "ApiKey" is the pre-Rust
                // spelling, kept so existing stored settings keep working.
                var token = Val("Token") ?? Val("ApiKey");
                if (token is { }) c["token"] = token;
                break;
            default:
                // All remaining cloud engines use a simple apiKey
                if (Val("ApiKey") is { } apiKey) c["apiKey"] = apiKey;
                break;
        }
        return c;
    }

    /// <summary>Parses a Watson TTS service URL into (region, instanceId).
    /// Returns false for anything that doesn't match the documented shape
    /// rather than guessing — a wrong region silently breaks auth.</summary>
    internal static bool TryParseWatsonServiceUrl(string url, out string region, out string instanceId)
    {
        (region, instanceId) = ("", "");
        var match = System.Text.RegularExpressions.Regex.Match(url,
            @"^https://api\.([a-z0-9-]+)\.text-to-speech\.watson\.cloud\.ibm\.com/instances/([0-9a-f-]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        region = match.Groups[1].Value.ToLowerInvariant();
        instanceId = match.Groups[2].Value;
        return true;
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var data = new
            {
                SelectedEngine,
                Credentials,
                SelectedVoiceId,
                SpeechRate = (int)SpeechRate,
                SpeechPitch = (int)SpeechPitch,
                SpeechVolume
            };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("SelectedEngine", out var engine))
                SelectedEngine = engine.GetString() ?? "sapi";
            if (root.TryGetProperty("Credentials", out var creds))
                Credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(creds.GetRawText()) ?? new();
            if (root.TryGetProperty("SelectedVoiceId", out var voice))
                SelectedVoiceId = voice.GetString();
            if (root.TryGetProperty("SpeechRate", out var rate) && rate.TryGetInt32(out var rateVal))
                SpeechRate = (SpeechRate)rateVal;
            if (root.TryGetProperty("SpeechPitch", out var pitch) && pitch.TryGetInt32(out var pitchVal))
                SpeechPitch = (SpeechPitch)pitchVal;
            if (root.TryGetProperty("SpeechVolume", out var vol) && vol.TryGetInt32(out var volVal))
                SpeechVolume = volVal;
        }
        catch { }
    }

    public void Dispose()
    {
        _sapiSynth?.Dispose();
        _sapiSynth = null;
        _client?.Dispose();
        _client = null;
    }
}
