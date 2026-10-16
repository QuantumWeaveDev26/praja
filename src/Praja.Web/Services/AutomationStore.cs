using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Praja.Web.Services;

public sealed record PreparedComplaint(Guid InboxId,int InboxVersion,string Fingerprint,string Source,bool IsDemo,IntakeAnalysis Analysis,DraftResult Draft,DateTimeOffset PreparedUtc);
public sealed record AutomationState
{
    public bool Enabled{get;init;}
    public string Mailbox{get;init;}="";
    public Guid Policy{get;init;}=Guid.NewGuid();
    public DateTimeOffset? LastRunUtc{get;init;}
    public string Status{get;init;}="Not started";
    public string[] Seen{get;init;}=[];
    public ManualMailReview[] ManualReview{get;init;}=[];
    public ManualMailReview[] NonComplaintReview{get;init;}=[];
    public string[] ForcedReview{get;init;}=[];
    public PreparedComplaint[] Prepared{get;init;}=[];
    public override string ToString()=>"Automation state (content redacted)";
}
public sealed record ManualMailReview(string Mailbox,string MessageId);
public sealed class AutomationStore
{
    private readonly string directory,scope;private readonly IDataProtector protector;private readonly bool local;private readonly object gate=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> runs=new();
    public AutomationStore(IConfiguration config,IDataProtectionProvider protection,IWebHostEnvironment environment)
    {
        local=environment.IsDevelopment()||environment.IsEnvironment("Testing");
        var identity=(config["Database:Provider"]??"Sqlite")+"|"+(config.GetConnectionString("Cases")??Path.Combine(environment.ContentRootPath,"App_Data","workspace.db"));
        scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        directory=config["Automation:SettingsDirectory"]??config["Ai:SettingsDirectory"]??Path.Combine(environment.ContentRootPath,"App_Data");
        protector=protection.CreateProtector("Praja.Automation.v1",scope);
    }
    public SemaphoreSlim RunLock(Guid owner)=>runs.GetOrAdd(owner,_=>new(1,1));
    private string PathFor(Guid owner)=>Path.Combine(directory,"automation-"+scope+"-"+owner.ToString("N")+".protected");
    public AutomationState Read(Guid owner)
    {
        lock(gate)
        {
            var path=PathFor(owner);if(!local||!File.Exists(path))return new();
            try{if(new FileInfo(path).Length>4_000_000)return new();return JsonSerializer.Deserialize<AutomationState>(protector.CreateProtector(owner.ToString("N")).Unprotect(File.ReadAllText(path)))??new();}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException){return new();}
        }
    }
    public AutomationState Update(Guid owner,Func<AutomationState,AutomationState> change)
    {
        if(!local)throw new InvalidOperationException("Automation is available in the local prototype only.");
        lock(gate)
        {
            var value=change(Read(owner));var json=JsonSerializer.Serialize(value);if(json.Length>2_500_000)throw new InvalidOperationException("The preparation queue is full. Review existing drafts first.");
            Directory.CreateDirectory(directory);var path=PathFor(owner);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,protector.CreateProtector(owner.ToString("N")).Protect(json));File.Move(temporary,path,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            return value;
        }
    }
}
