using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Praja.Web.Services;

// Contains credentials. Only explicitly selected non-secret properties may be rendered.
public sealed record XConfiguration
{
    public string BearerToken { get; init; } = "";
    public string Query { get; init; } = XConfigurationStore.DefaultQuery;
    public Guid Version { get; init; } = Guid.Empty;
    public string NextToken { get; init; } = "";
    public DateTimeOffset? LastCheckedUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string Status { get; init; } = "Not configured";
    public bool Configured => BearerToken.Length > 0;
    public override string ToString() => "X configuration (credentials redacted)";
}

public sealed class XConfigurationStore
{
    public const string DefaultQuery = "(Karnataka OR Bengaluru OR Bangalore OR Mysuru OR Mysore OR ಕರ್ನಾಟಕ OR ಬೆಂಗಳೂರು OR ಮೈಸೂರು) (pothole OR garbage OR sewage OR \"water supply\" OR \"power cut\" OR ದೂರು OR ನೀರಿಲ್ಲ OR ಕಸ OR ಚರಂಡಿ) -is:retweet";
    private readonly IDataProtector protector;
    private readonly string directory, scope;
    private readonly bool local;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> runs = new();
    public XConfigurationStore(IConfiguration config, IDataProtectionProvider protection, IWebHostEnvironment environment)
    {
        local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var identity = (config["Database:Provider"] ?? "Sqlite") + "|" + (config.GetConnectionString("Cases") ?? Path.Combine(environment.ContentRootPath, "App_Data", "workspace.db"));
        scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        directory = config["X:SettingsDirectory"] ?? config["Ai:SettingsDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data");
        protector = protection.CreateProtector("Praja.LocalXConfiguration.v1", scope);
    }
    public SemaphoreSlim RunLock(Guid owner) => runs.GetOrAdd(owner, _ => new(1, 1));
    private string PathFor(Guid owner) => Path.Combine(directory, "x-" + scope + "-" + owner.ToString("N") + ".protected");
    public XConfiguration Read(Guid owner)
    {
        lock (gate)
        {
            var path = PathFor(owner);
            if (!local || !File.Exists(path)) return new();
            try
            {
                if (new FileInfo(path).Length > 128_000) return new();
                return JsonSerializer.Deserialize<XConfiguration>(protector.CreateProtector(owner.ToString("N")).Unprotect(File.ReadAllText(path))) ?? new();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
        }
    }
    public void Save(Guid owner, string? token, string query)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 512 || query.Any(char.IsControl)) throw new InvalidOperationException("Enter a search query of 1–512 characters without control characters.");
        Update(owner, current =>
        {
            var credential = string.IsNullOrWhiteSpace(token) ? current.BearerToken : token.Trim();
            if (credential.Length is < 10 or > 4096 || credential.Any(c => c < 33 || c > 126)) throw new InvalidOperationException("Enter the bearer token directly in the credential field.");
            return new() { BearerToken = credential, Query = query, Version = Guid.NewGuid(), Status = "Saved; search access has not been tested" };
        });
    }
    internal XConfiguration Update(Guid owner, Func<XConfiguration, XConfiguration> change)
    {
        if (!local) throw new InvalidOperationException("X collection is available in the local prototype only.");
        lock (gate)
        {
            var value = change(Read(owner));
            Directory.CreateDirectory(directory);
            var path = PathFor(owner);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, protector.CreateProtector(owner.ToString("N")).Protect(JsonSerializer.Serialize(value)));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return value;
        }
    }
    public void Disconnect(Guid owner) => Update(owner, current => new() { Query = current.Query, Version = Guid.NewGuid(), Status = "Disconnected; retained inbox posts remain available" });
}
