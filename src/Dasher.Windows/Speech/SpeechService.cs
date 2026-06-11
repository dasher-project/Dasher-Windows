using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    public List<TtsVoice> AvailableVoices { get; private set; } = [];
    public bool IsSpeaking { get; private set; }
    public bool IsLoadingVoices { get; private set; }
    public string? ErrorMessage { get; private set; }
    public SpeechRate SpeechRate { get; set; } = SpeechRate.Medium;
    public SpeechPitch SpeechPitch { get; set; } = SpeechPitch.Medium;
    public int SpeechVolume { get; set; } = 80;

    public static readonly string[] EngineNames =
    [
        "sapi", "azure", "google", "polly", "openai", "elevenlabs",
        "watson", "playht", "witai", "gemini", "cartesia", "deepgram",
        "hume", "xai", "fishaudio", "mistral", "murf", "unrealspeech",
        "resemble", "upliftai", "modelslab", "sherpaonnx"
    ];

    public static string EngineDisplayName(string engine) => engine switch
    {
        "sapi" => "SAPI (Windows)",
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
        "google" => ["KeyFilePath"],
        "polly" => ["AccessKeyId", "SecretAccessKey", "Region"],
        "openai" => ["ApiKey"],
        "elevenlabs" => ["ApiKey"],
        "watson" => ["ApiKey", "ServiceUrl"],
        "playht" => ["ApiKey", "UserId"],
        "witai" => ["ApiKey"],
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
            var client = GetOrCreateClient();
            if (client == null)
            {
                AvailableVoices = [];
                return;
            }
            var voices = await client.GetVoicesAsync();
            AvailableVoices = voices;
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

    public void Stop()
    {
        try
        {
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
        _client?.Dispose();
        try
        {
            var creds = BuildCredentials();
            _client = TtsFactory.CreateClient(SelectedEngine, creds);
            _needsRecreate = false;
            return _client;
        }
        catch
        {
            _client = null;
            return null;
        }
    }

    private ITtsCredentials? BuildCredentials()
    {
        return SelectedEngine switch
        {
            "sapi" => null,
            "azure" => new AzureCredentials
            {
                SubscriptionKey = Credentials.GetValueOrDefault("SubscriptionKey", ""),
                Region = Credentials.GetValueOrDefault("Region", "")
            },
            "google" => new GoogleCredentials
            {
                KeyFilePath = Credentials.GetValueOrDefault("KeyFilePath", "")
            },
            "polly" => new PollyCredentials
            {
                AccessKeyId = Credentials.GetValueOrDefault("AccessKeyId", ""),
                SecretAccessKey = Credentials.GetValueOrDefault("SecretAccessKey", ""),
                Region = Credentials.GetValueOrDefault("Region", "us-east-1")
            },
            "openai" => new OpenAICredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "elevenlabs" => new ElevenLabsCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "watson" => new WatsonCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", ""),
                ServiceUrl = Credentials.GetValueOrDefault("ServiceUrl", "")
            },
            "playht" => new PlayHtCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", ""),
                UserId = Credentials.GetValueOrDefault("UserId", "")
            },
            "witai" => new WitAiCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "gemini" => new GeminiCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "cartesia" => new CartesiaCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "deepgram" => new DeepgramCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "hume" => new HumeCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "xai" => new XaiCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "fishaudio" => new FishAudioCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "mistral" => new MistralCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "murf" => new MurfCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "unrealspeech" => new UnrealSpeechCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "resemble" => new ResembleCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "upliftai" => new UpliftAiCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "modelslab" => new ModelsLabCredentials
            {
                ApiKey = Credentials.GetValueOrDefault("ApiKey", "")
            },
            "sherpaonnx" => new SherpaOnnxCredentials
            {
                ModelPath = Credentials.GetValueOrDefault("ModelPath", ""),
                ModelId = Credentials.GetValueOrDefault("ModelId", "")
            },
            _ => null
        };
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
        _client?.Dispose();
        _client = null;
    }
}
