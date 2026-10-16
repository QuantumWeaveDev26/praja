using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Praja.Web.Services;

// Never render or log this record: it contains OAuth credentials and tokens.
public sealed record GmailConfiguration
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Mailbox { get; init; } = "team.quantumweaveai@gmail.com";
    public string LabelName { get; init; } = "Praja-Complaints";
    public Guid Version { get; init; } = Guid.NewGuid();
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset AccessExpiresUtc { get; init; }
    public string VerifiedMailbox { get; init; } = "";
    public DateTimeOffset? VerifiedUtc { get; init; }
    public string PendingNonce { get; init; } = "";
    public bool Configured => ClientId.Length > 0 && ClientSecret.Length > 0;
    public bool Connected => Configured && RefreshToken.Length > 0 && VerifiedMailbox == Mailbox;
    public override string ToString() => "Gmail configuration (credentials redacted)";
}

public sealed class GmailConfigurationStore
{
    private readonly IDataProtector protector;
    private readonly string directory;
    private readonly string scope;
    private readonly bool local;
    private readonly object gate = new();
    public GmailConfigurationStore(IConfiguration config, IDataProtectionProvider protection, IWebHostEnvironment environment)
    {
        local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var identity = (config["Database:Provider"] ?? "Sqlite") + "|" + (config.GetConnectionString("Cases") ?? Path.Combine(environment.ContentRootPath,"App_Data","workspace.db"));
        scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        protector = protection.CreateProtector("Praja.LocalGmailConfiguration.v1",scope);
        directory = config["Gmail:SettingsDirectory"] ?? config["Ai:SettingsDirectory"] ?? Path.Combine(environment.ContentRootPath,"App_Data");
    }
    private string PathFor(Guid owner) => Path.Combine(directory,"gmail-"+scope+"-"+owner.ToString("N")+".protected");
    public GmailConfiguration Read(Guid owner)
    {
        lock(gate)
        {
            var path=PathFor(owner);
            if(!local || !File.Exists(path))return new();
            try
            {
                if(new FileInfo(path).Length>128_000)return new();
                return JsonSerializer.Deserialize<GmailConfiguration>(protector.CreateProtector(owner.ToString("N")).Unprotect(File.ReadAllText(path)))??new();
            }
            catch(Exception ex)when(ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException){return new();}
        }
    }
    public void Save(Guid owner,string clientId,string? secret,string mailbox,string label)
    {
        clientId=clientId.Trim();mailbox=mailbox.Trim().ToLowerInvariant();label=label.Trim();
        if(clientId.Length>200 || !Regex.IsMatch(clientId,@"^[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$"))throw new InvalidOperationException("Enter the complete Google web application's client ID ending in .apps.googleusercontent.com.");
        if(mailbox.Length is <5 or >200 || !new EmailAddressAttribute().IsValid(mailbox))throw new InvalidOperationException("Enter the Gmail address you intend to connect.");
        if(label.Length is <1 or >80 || !Regex.IsMatch(label,@"^[A-Za-z0-9 _/-]+$"))throw new InvalidOperationException("Use a complaint label of 1–80 letters, numbers, spaces, dashes, underscores or slashes.");
        Update(owner,current=>
        {
            var credential=string.IsNullOrWhiteSpace(secret)&&current.ClientId==clientId?current.ClientSecret:secret?.Trim()??"";
            if(credential.Length is <10 or >300 || credential.Any(c=>c<33||c>126))throw new InvalidOperationException("Enter the client secret directly here. A new client ID needs its matching secret.");
            return new(){ClientId=clientId,ClientSecret=credential,Mailbox=mailbox,LabelName=label};
        });
    }
    public GmailConfiguration Update(Guid owner,Func<GmailConfiguration,GmailConfiguration> change)
    {
        if(!local)throw new InvalidOperationException("Gmail local configuration is unavailable in production.");
        lock(gate)
        {
            var value=change(Read(owner));Directory.CreateDirectory(directory);
            var path=PathFor(owner);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,protector.CreateProtector(owner.ToString("N")).Protect(JsonSerializer.Serialize(value)));File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return value;
        }
    }
    public void Disconnect(Guid owner)=>Update(owner,current=>current with{Version=Guid.NewGuid(),AccessToken="",RefreshToken="",VerifiedMailbox="",VerifiedUtc=null,AccessExpiresUtc=default,PendingNonce=""});
}
