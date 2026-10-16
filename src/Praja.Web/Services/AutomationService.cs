using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Domain;

namespace Praja.Web.Services;

public sealed class AutomationService(WorkspaceDb db,AutomationStore store,GmailConfigurationStore gmailSettings,GmailService gmail,InboxService inbox,DraftingService drafting,TimeProvider clock)
{
    public static bool LooksLikeComplaint(string text)
    {
        if(Regex.IsMatch(text,@"\b(complaint|grievance|unresolved|potholes?|sewage)\b|ದೂರು|ಸಮಸ್ಯೆ",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))return true;
        var issue=Regex.IsMatch(text,@"\b(no water|no electricity|not working|not collected|not paid|not received|not available|uncollected|blocked|broken|damaged|leak|shortage|power cut|please restore|please repair|please help)\b|ನೀರಿಲ್ಲ|ನೀರು ಇಲ್ಲ|ಶಿಕ್ಷಕರಿಲ್ಲ|ಕಸ ಸಂಗ್ರಹವಾಗಿಲ್ಲ|ವಿದ್ಯುತ್ ಇಲ್ಲ|ಚರಂಡಿ ಕಟ್ಟಿಕೊಂಡಿದೆ",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return issue;
    }
    public static IntakeAnalysis WithLocalQuotes(string text,IntakeAnalysis analysis)
    {
        // Only explicit source wording is copied; no locality-to-district inference.
        var districts=Regex.Matches(text,@"(?<![\p{L}])(?:Bengaluru Urban|Bengaluru Rural|Mysuru|Dharwad|Ballari|Belagavi|ಮೈಸೂರು|ಧಾರವಾಡ|ಬೆಳಗಾವಿ)(?![\p{L}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var wards=Regex.Matches(text,@"(?:\bWard\s*[-:]?\s*\d+\b|ವಾರ್ಡ್\s*[-:]?\s*[0-9೦-೯]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return analysis with{District=analysis.District.Length>0?analysis.District:districts.Length==1?districts[0]:"",Location=analysis.Location.Length>0?analysis.Location:wards.Length==1?wards[0]:""};
    }
    public async Task RunAsync(Guid owner,CancellationToken cancellation=default)
    {
        var run=store.RunLock(owner);if(!await run.WaitAsync(0,cancellation))throw new InvalidOperationException("A collection run is already active. Refresh the queue shortly.");
        try
        {
            var policy=store.Read(owner);if(!policy.Enabled)throw new InvalidOperationException("Enable automation once before collecting Inbox mail.");
            var seen=policy.Seen.ToHashSet(StringComparer.Ordinal);var accepted=0;var ignored=0;var unsupported=0;var scanned=0;var retryable=0;
            if(gmailSettings.Read(owner).Connected)
            {
                if(policy.Mailbox!=gmailSettings.Read(owner).Mailbox)throw new InvalidOperationException("The connected mailbox changed. Enable automation again for the mailbox shown on this page.");
                var mailboxKey=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policy.Mailbox)))[..24];
                var forced=policy.ForcedReview.Where(id=>id.StartsWith("gmail:"+mailboxKey+":",StringComparison.Ordinal)).Take(20).ToHashSet(StringComparer.Ordinal);
                seen.ExceptWith(forced);
                var batch=await gmail.ReadInboxAsync(owner,seen,cancellation,()=>Current(owner,policy.Policy),forced.Count>0?forced.Select(id=>id.Split(':').Last()).ToArray():null);scanned=batch.Scanned;unsupported=batch.Unsupported.Count;retryable=batch.Retryable;
                foreach(var id in batch.Unsupported)seen.Add(id);
                store.Update(owner,s=>s with{ForcedReview=s.ForcedReview.Except(batch.Unsupported).ToArray(),NonComplaintReview=s.NonComplaintReview.Where(r=>r.Mailbox!=policy.Mailbox||!batch.Unsupported.Contains("gmail:"+mailboxKey+":"+r.MessageId)).ToArray()});
                store.Update(owner,s=>s with{ManualReview=s.ManualReview.Concat(batch.Unsupported.Select(id=>new ManualMailReview(policy.Mailbox,id.Split(':').Last()))).Distinct().TakeLast(50).ToArray()});
                foreach(var mail in batch.Messages)
                {
                    if(!Current(owner,policy.Policy))break;
                    if(!forced.Contains(mail.ExternalId)&&!LooksLikeComplaint(mail.Original))
                    {
                        seen.Add(mail.ExternalId);ignored++;
                        store.Update(owner,s=>s with{NonComplaintReview=s.NonComplaintReview.Append(new ManualMailReview(policy.Mailbox,mail.ExternalId.Split(':').Last())).Distinct().TakeLast(50).ToArray()});
                        continue;
                    }
                    await inbox.CaptureAsync(owner,forced.Contains(mail.ExternalId)?"Officer-requested Gmail recheck":"Automatic Gmail intake",mail.Original,"Email",mail.Language,mail.SourceUrl,mail.ExternalId,true,cancellation);
                    seen.Add(mail.ExternalId);accepted++;
                    store.Update(owner,s=>s with{Seen=s.Seen.Concat(seen).Distinct().TakeLast(2000).ToArray(),
                        ForcedReview=s.ForcedReview.Where(id=>id!=mail.ExternalId).ToArray(),
                        NonComplaintReview=s.NonComplaintReview.Where(r=>r.Mailbox!=policy.Mailbox||r.MessageId!=mail.ExternalId.Split(':').Last()).ToArray()});
                }
            }
            var prepared=0;var failures=0;
            var pending=await db.InboxMessages.AsNoTracking().Where(m=>m.OwnerId==owner&&m.State=="Pending"&&m.CapturedBy!="X public search").OrderBy(m=>m.ReceivedUtc).Take(100).ToListAsync(cancellation);
            var activeIds=await db.InboxMessages.AsNoTracking().Where(m=>m.OwnerId==owner&&m.State=="Pending").Select(m=>new{m.Id,m.Version,m.Fingerprint}).ToListAsync(cancellation);
            store.Update(owner,s=>s with{Prepared=s.Prepared.Where(p=>activeIds.Any(m=>m.Id==p.InboxId&&m.Version==p.InboxVersion&&m.Fingerprint==p.Fingerprint)).ToArray()});
            foreach(var item in pending)
            {
                if(!Current(owner,policy.Policy)||prepared+failures>=5)break;
                if(store.Read(owner).Prepared.Any(p=>p.InboxId==item.Id))continue;
                try{await PrepareAsync(item,"Automatic intake",false,cancellation,policy.Policy);prepared++;}
                catch(Exception ex)when(ex is InvalidOperationException or IOException or System.Text.Json.JsonException){failures++;db.ChangeTracker.Clear();}
            }
            store.Update(owner,s=>s with{Seen=s.Seen.Concat(seen).Distinct().TakeLast(2000).ToArray(),LastRunUtc=clock.GetUtcNow(),Status=s.Policy!=policy.Policy?s.Status:$"Scanned {scanned} IDs; retained {accepted} likely complaints; prepared {prepared} drafts; {ignored} not detected as complaints, {unsupported} unsupported, {retryable} collection/OCR failures awaiting retry, {failures} preparation failures. Draft preparation failures remain pending for retry. Unsupported emails require manual review in Gmail; no partial text was retained."});
        }
        catch(Exception ex)when(ex is InvalidOperationException or IOException or System.Text.Json.JsonException or DbUpdateException)
        {store.Update(owner,s=>s with{LastRunUtc=clock.GetUtcNow(),Status="Run incomplete. Check Gmail connection and local file access; retained messages remain available and the next run retries pending work."});throw new InvalidOperationException("Collection did not finish. Check the connection and queue status; existing messages were retained.");}
        finally{run.Release();}
    }
    private bool Current(Guid owner,Guid policy){var current=store.Read(owner);return current.Enabled&&current.Policy==policy;}
    private async Task PrepareAsync(InboxMessage item,string source,bool demo,CancellationToken cancellation,Guid? policy=null)
    {
        if(store.Read(item.OwnerId).Prepared.Count(p=>p.InboxId!=item.Id)>=100)throw new InvalidOperationException("Review existing prepared messages before preparing more.");
        var analysis=WithLocalQuotes(item.OriginalText,await drafting.AnalyzeAsync(item.OriginalText,true,cancellation));
        if(policy.HasValue&&!Current(item.OwnerId,policy.Value))throw new InvalidOperationException("Automation was paused or changed.");
        var draft=await drafting.DraftAsync(item.OriginalText,item.Language,analysis.District,analysis.Location,cancellation);
        demo=demo||InboxImportService.IsDemoSample(item)||(item.OriginalText.StartsWith("Subject: PRAJA DEMO TEST",StringComparison.OrdinalIgnoreCase)&&item.OriginalText.Contains("Fictional test only.",StringComparison.OrdinalIgnoreCase));
        if(demo)draft=draft with{Text="Fictional prototype test.\n\n"+draft.Text};
        var proposal=new PreparedComplaint(item.Id,item.Version,item.Fingerprint,source,demo,analysis,draft,clock.GetUtcNow());
        store.Update(item.OwnerId,state=>state with{Prepared=state.Prepared.Where(p=>p.InboxId!=item.Id).Append(proposal).ToArray()});
    }
    public static readonly (string Platform,string Id,string Text)[] SocialDemo=
    [
        ("X","praja-auto-demo-x-001","[Fictional X demo] No water supply in Ward 12, Mysuru, for three days. Please restore supply."),
        ("Facebook","praja-auto-demo-facebook-001","[Fictional Facebook demo] Uncollected garbage is blocking Ward 12, Mysuru. Please clear the garbage."),
        ("Instagram","praja-auto-demo-instagram-001","[Fictional Instagram demo] ಮೈಸೂರು ವಾರ್ಡ್ 12 ಸರ್ಕಾರಿ ಶಾಲೆಯಲ್ಲಿ ಶಿಕ್ಷಕರಿಲ್ಲ. ದಯವಿಟ್ಟು ಶಿಕ್ಷಕರನ್ನು ನೇಮಿಸಿ.")
    ];
    public async Task DemoAsync(Guid owner,CancellationToken cancellation=default)
    {
        var run=store.RunLock(owner);if(!await run.WaitAsync(0,cancellation))throw new InvalidOperationException("A preparation run is already active.");
        try
        {
            foreach(var post in SocialDemo)
            {
                var item=await inbox.CaptureAsync(owner,"Simulated "+post.Platform+" feed",post.Text,"Social media",post.Platform=="Instagram"?"Kannada":"English","https://social.example.test/"+post.Platform.ToLowerInvariant()+"/"+post.Id,post.Id,true,cancellation);
                if(item.State=="Pending"&&!store.Read(owner).Prepared.Any(p=>p.InboxId==item.Id))await PrepareAsync(item,post.Platform+" · simulated feed",true,cancellation);
            }
            store.Update(owner,s=>s with{Status="Three fictional social posts collected and prepared. No social platform was contacted; repeated demo runs reuse the source IDs.",LastRunUtc=clock.GetUtcNow()});
        }
        finally{run.Release();}
    }
}

public sealed class IntakeAutomationWorker(IServiceScopeFactory scopes,IWebHostEnvironment environment,ILogger<IntakeAutomationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!environment.IsDevelopment())return;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(60));
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var store=scope.ServiceProvider.GetRequiredService<AutomationStore>();
                foreach(var owner in await db.Operators.Where(o=>!o.IsDisabled).Select(o=>o.Id).ToListAsync(stoppingToken))
                    if(store.Read(owner).Enabled)try{await scope.ServiceProvider.GetRequiredService<AutomationService>().RunAsync(owner,stoppingToken);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning("Automatic intake run did not finish ({Type}); check the queue status.",ex.GetType().Name);}
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Automatic intake worker unavailable ({Type}).",ex.GetType().Name);}
            try{if(!await timer.WaitForNextTickAsync(stoppingToken))break;}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}
