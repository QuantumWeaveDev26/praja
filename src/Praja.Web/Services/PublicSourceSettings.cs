using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Praja.Web.Services;

public sealed record PublicSourceConfiguration
{
    public string YouTubeKey { get; init; } = "";
    public string Query { get; init; } = "Bengaluru water";
    public Guid Version { get; init; }
    public string Status { get; init; } = "No collection run yet";
    public override string ToString() => "Public source settings (key redacted)";
}

public sealed class PublicSourceSettings
{
    private readonly IDataProtector protector;
    private readonly string directory, scope;
    private readonly bool local;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> runs = new();
    public PublicSourceSettings(IConfiguration config, IDataProtectionProvider protection, IWebHostEnvironment environment)
    {
        local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var identity = (config["Database:Provider"] ?? "Sqlite") + "|" + (config.GetConnectionString("Cases") ?? Path.Combine(environment.ContentRootPath, "App_Data", "workspace.db"));
        scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        directory = config["PublicSources:SettingsDirectory"] ?? config["Ai:SettingsDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data");
        protector = protection.CreateProtector("Praja.PublicSources.v1", scope);
    }
    public SemaphoreSlim RunLock(Guid owner) => runs.GetOrAdd(owner, _ => new(1, 1));
    private string PathFor(Guid owner) => Path.Combine(directory, "public-sources-" + scope + "-" + owner.ToString("N") + ".protected");
    public PublicSourceConfiguration Read(Guid owner)
    {
        lock (gate)
        {
            if (!local || !File.Exists(PathFor(owner))) return new();
            try
            {
                if (new FileInfo(PathFor(owner)).Length > 64_000) return new();
                return JsonSerializer.Deserialize<PublicSourceConfiguration>(protector.CreateProtector(owner.ToString("N")).Unprotect(File.ReadAllText(PathFor(owner)))) ?? new();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
        }
    }
    public void Save(Guid owner, string? key, string query, bool removeKey = false)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 200 || query.Any(char.IsControl)) throw new InvalidOperationException("Enter a place and issue in 2–200 characters without control characters.");
        Update(owner, current =>
        {
            var credential = removeKey ? "" : string.IsNullOrWhiteSpace(key) ? current.YouTubeKey : key.Trim();
            if (credential.Length > 256 || credential.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidOperationException("Enter the YouTube Data API key directly in the key field.");
            return current with { YouTubeKey = credential, Query = query, Version = Guid.NewGuid(), Status = "Settings saved; no request made" };
        });
    }
    internal void Update(Guid owner, Func<PublicSourceConfiguration, PublicSourceConfiguration> change)
    {
        if (!local) throw new InvalidOperationException("Public source collection is available in the local prototype only.");
        lock (gate)
        {
            var state = change(Read(owner));
            Directory.CreateDirectory(directory);
            var path = PathFor(owner); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, protector.CreateProtector(owner.ToString("N")).Protect(JsonSerializer.Serialize(state)));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
