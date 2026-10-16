using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Praja.Web.Services;

// Never render or log this object: it holds the private provider credential.
public sealed record AiConfiguration(string ApiKey, string Endpoint, string Model, string TranscriptionEndpoint, string TranscriptionModel)
{
    public static AiConfiguration From(IConfiguration config) => new(config["Ai:ApiKey"] ?? "", config["Ai:Endpoint"] ?? "", config["Ai:Model"] ?? "", config["Ai:TranscriptionEndpoint"] ?? "", config["Ai:TranscriptionModel"] ?? "");
    public bool DraftingConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);
    public bool SpeechConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(TranscriptionEndpoint) && !string.IsNullOrWhiteSpace(TranscriptionModel);
    public override string ToString() => "AI configuration (credential redacted)";
}

public sealed class AiConfigurationStore
{
    private readonly IConfiguration config;
    private readonly IDataProtector protector;
    private readonly string path;
    private readonly bool local;
    private readonly object gate = new();
    public AiConfigurationStore(IConfiguration config, IDataProtectionProvider protection, IWebHostEnvironment environment)
    {
        this.config = config;
        local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var workspace = (config["Database:Provider"] ?? "Sqlite") + "|" + (config.GetConnectionString("Cases") ?? Path.Combine(environment.ContentRootPath, "App_Data", "workspace.db"));
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)));
        protector = protection.CreateProtector("Praja.LocalAiConfiguration.v1", scope);
        path = Path.Combine(config["Ai:SettingsDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data"), "ai-" + scope + ".protected");
    }

    public AiConfiguration Read()
    {
        lock (gate)
        {
            if (!local || !File.Exists(path)) return AiConfiguration.From(config);
            try
            {
                if (new FileInfo(path).Length > 16_000) return Empty;
                return JsonSerializer.Deserialize<AiConfiguration>(protector.Unprotect(File.ReadAllText(path))) ?? Empty;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return Empty; }
        }
    }

    public void SaveOpenAi(string? key, string model, bool enableSpeech, string? speechModel)
    {
        if (!local) throw new InvalidOperationException("Local AI settings are unavailable in production.");
        lock (gate)
        {
            key = string.IsNullOrWhiteSpace(key) ? Read().ApiKey : key.Trim();
            if (key.Length is < 10 or > 500 || key.Any(c => c < 33 || c > 126)) throw new InvalidOperationException("Enter a valid API key directly in this form.");
            if (!ValidModel(model) || enableSpeech && !ValidModel(speechModel)) throw new InvalidOperationException("Enter a model ID using letters, numbers, dots, dashes or underscores (up to 100 characters).");
            Write(new(key, "https://api.openai.com/v1/chat/completions", model, enableSpeech ? "https://api.openai.com/v1/audio/transcriptions" : "", enableSpeech ? speechModel! : ""));
        }
    }
    public void Disable()
    {
        if (!local) throw new InvalidOperationException("Local AI settings are unavailable in production.");
        lock (gate) Write(Empty);
    }
    private void Write(AiConfiguration settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(settings)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool ValidModel(string? model) => model is { Length: > 0 and <= 100 } && Regex.IsMatch(model, "^[a-zA-Z0-9._-]+$");
    private static AiConfiguration Empty => new("", "", "", "", "");
}
