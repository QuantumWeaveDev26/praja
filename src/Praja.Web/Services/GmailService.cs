using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Praja.Web.Services;

public sealed record GmailPreview(ImportPreview? Preview,IReadOnlyList<string> Notes);
public sealed record AutomationMail(string ExternalId,string Original,string Language,string SourceUrl);
public sealed record AutomationMailBatch(IReadOnlyList<AutomationMail> Messages,IReadOnlyList<string> Unsupported,int Scanned,int Retryable=0);
public sealed partial class GmailService(GmailConfigurationStore settings,IHttpClientFactory clients,IDataProtectionProvider protection,
    TimeProvider clock,InboxImportService imports,MediaIntakeService? media=null)
{
    public const string Scope="https://www.googleapis.com/auth/gmail.readonly";
    private sealed class OversizedGmailContentException():InvalidOperationException("Google returned too much content. Use smaller complaint emails.");
    private readonly IDataProtector stateProtector=protection.CreateProtector("Praja.GmailOAuthState.v1");
    private sealed record OAuthState(Guid Owner,Guid Configuration,string Nonce,string RedirectUri,string Verifier,DateTimeOffset Expires);
    public static string CallbackUrl(HttpRequest request)
    {
        if(request.Scheme is not ("http" or "https") || request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
            throw new InvalidOperationException("Open this local configuration using http://127.0.0.1:5080/GmailSettings.");
        return request.Scheme+"://"+request.Host+"/Connections/GmailCallback";
    }
    public string Start(Guid owner,string redirectUri,bool permission)
    {
        if(!permission)throw new InvalidOperationException("Confirm permission to connect this mailbox with read-only Gmail access.");
        if(!Uri.TryCreate(redirectUri,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||!uri.IsLoopback||uri.AbsolutePath!="/Connections/GmailCallback"||uri.Query.Length>0||uri.Fragment.Length>0||uri.UserInfo.Length>0)
            throw new InvalidOperationException("Use the local callback shown by Praja.");
        var current=settings.Update(owner,value=>value.Configured?value with{Version=Guid.NewGuid(),PendingNonce=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}:throw new InvalidOperationException("Save the client ID and secret before connecting Gmail."));
        var verifier=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state=stateProtector.Protect(JsonSerializer.Serialize(new OAuthState(owner,current.Version,current.PendingNonce,redirectUri,verifier,clock.GetUtcNow().AddMinutes(10))));
        return QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth",new Dictionary<string,string?>
        {
            ["client_id"]=current.ClientId,["redirect_uri"]=redirectUri,["response_type"]="code",["scope"]=Scope,
            ["state"]=state,["access_type"]="offline",["prompt"]="consent",["login_hint"]=current.Mailbox,
            ["code_challenge"]=WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),["code_challenge_method"]="S256"
        });
    }
    public async Task CompleteAsync(Guid owner,string? stateToken,string? code,string? error,CancellationToken cancellation=default)
    {
        OAuthState state;
        try
        {
            if(stateToken is null || stateToken.Length>8192)throw new InvalidOperationException();
            state=JsonSerializer.Deserialize<OAuthState>(stateProtector.Unprotect(stateToken))??throw new InvalidOperationException();
            if(state.Owner!=owner||state.Expires<=clock.GetUtcNow())throw new InvalidOperationException();
        }
        catch(Exception ex)when(ex is CryptographicException or JsonException or InvalidOperationException or ArgumentException or FormatException)
        {throw new InvalidOperationException("This connection request expired or changed. Start Connect Gmail again.");}
        var current=settings.Update(owner,value=>value.Version==state.Configuration&&value.PendingNonce==state.Nonce
            ?value with{PendingNonce=""}:throw new InvalidOperationException("This connection request was already used or changed. Start Connect Gmail again."));
        if(!string.IsNullOrEmpty(error))throw new InvalidOperationException("Google permission was not completed. Connect Gmail again when you are ready.");
        if(string.IsNullOrWhiteSpace(code)||code.Length>4096)throw new InvalidOperationException("Google did not return a valid authorization code. Connect Gmail again.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response=await Token(new(){["client_id"]=current.ClientId,["client_secret"]=current.ClientSecret,["code"]=code,["grant_type"]="authorization_code",["redirect_uri"]=state.RedirectUri,["code_verifier"]=state.Verifier},deadline.Token);
        var access=Credential(response.RootElement,"access_token");var refresh=Credential(response.RootElement,"refresh_token");
        if(!response.RootElement.TryGetProperty("scope",out var scopes)||!scopes.GetString()!.Split(' ').Contains(Scope))throw new InvalidOperationException("Google did not grant read-only Gmail access. Reconnect and approve the requested Gmail permission.");
        using var profile=await Get("profile",access,deadline.Token);
        var email=(profile.RootElement.GetProperty("emailAddress").GetString()??"").ToLowerInvariant();
        if(email!=current.Mailbox)throw new InvalidOperationException("The Google account does not match the configured mailbox. Choose that mailbox when reconnecting.");
        var expires=Expiry(response.RootElement);
        settings.Update(owner,value=>value.Version==current.Version?value with{AccessToken=access,RefreshToken=refresh,AccessExpiresUtc=expires,VerifiedMailbox=email,VerifiedUtc=clock.GetUtcNow()}
            :throw new InvalidOperationException("Configuration changed while connecting. Reconnect using the current settings."));
    }
    public async Task<GmailPreview> PreviewAsync(Guid owner,string actor,CancellationToken cancellation=default)
    {
        var current=settings.Read(owner);if(!current.Connected)throw new InvalidOperationException("Connect Gmail before fetching complaint messages.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if(current.AccessExpiresUtc<=clock.GetUtcNow().AddSeconds(60))
        {
            using var refreshed=await Token(new(){["client_id"]=current.ClientId,["client_secret"]=current.ClientSecret,["refresh_token"]=current.RefreshToken,["grant_type"]="refresh_token"},deadline.Token);
            var token=Credential(refreshed.RootElement,"access_token");var expiry=Expiry(refreshed.RootElement);
            current=settings.Update(owner,value=>value.Version==current.Version&&value.Connected?value with{AccessToken=token,AccessExpiresUtc=expiry}:throw new InvalidOperationException("Gmail configuration changed. Reconnect before fetching messages."));
        }
        using var labelResponse=await Get("labels?fields=labels(id,name,type)",current.AccessToken,deadline.Token);
        var missingLabel=$"Gmail is connected, but custom label \"{current.LabelName}\" was not found in \"{current.Mailbox}\". In that Gmail account, create this exact label name (case-sensitive), apply it to a complaint email, then select Preview Gmail messages. No emails were imported.";
        if(!labelResponse.RootElement.TryGetProperty("labels",out var labels))throw new InvalidOperationException(missingLabel);
        var matches=labels.EnumerateArray().Where(label=>label.GetProperty("name").GetString()==current.LabelName).ToList();
        if(matches.Count!=1)throw new InvalidOperationException(missingLabel);
        if(matches[0].GetProperty("type").GetString()!="user")
            throw new InvalidOperationException($"The configured label \"{current.LabelName}\" is a built-in Gmail label. Built-in labels such as Inbox cannot be used for complaint intake. Create a custom label in \"{current.Mailbox}\", enter its exact name under Complaint label in Gmail, save and reconnect. No emails were imported.");
        var labelId=matches[0].GetProperty("id").GetString()??"";
        if(!Regex.IsMatch(labelId,@"^[A-Za-z0-9_-]{1,150}$"))throw new InvalidOperationException("Gmail returned an unsupported label ID.");
        var query="-in:spam -in:trash";
        using var list=await Get("messages?maxResults=5&labelIds="+Uri.EscapeDataString(labelId)+"&q="+Uri.EscapeDataString(query),current.AccessToken,deadline.Token);
        var notes=new List<string>();var rows=new List<string>();
        if(!list.RootElement.TryGetProperty("messages",out var messageList)||messageList.GetArrayLength()==0)
            return new(null,["No messages matched the complaint label. Create that custom label in Gmail and apply it to complaint emails, then preview again."]);
        notes.Add("Only the first five matching emails are considered per preview. HTML is converted to text. Up to three UTF-8 .txt or text-PDF attachments (64 KiB each) are extracted locally. Unsupported or oversized content excludes the whole email; binaries are not retained. No Gmail messages are changed.");
        var mailboxKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Mailbox)))[..24];
        foreach(var entry in messageList.EnumerateArray().Take(5))
        {
            var id=entry.GetProperty("id").GetString()??"";
            if(!Regex.IsMatch(id,@"^[A-Za-z0-9_-]{1,100}$"))throw new InvalidOperationException("Gmail returned an unsupported message ID.");
            using var message=await Get("messages/"+id+"?format=full",current.AccessToken,deadline.Token);
            if(message.RootElement.GetProperty("id").GetString()!=id)throw new InvalidOperationException("Gmail returned a different message than requested.");
            if(!message.RootElement.TryGetProperty("labelIds",out var messageLabels)||!messageLabels.EnumerateArray().Any(label=>label.GetString()==labelId)
                ||messageLabels.EnumerateArray().Any(label=>label.GetString() is "SPAM" or "TRASH"))
            {notes.Add("Message "+id+": its complaint label changed or it moved to spam/trash. It was excluded.");continue;}
            var payload=message.RootElement.GetProperty("payload");string? plain;
            try { plain=await ReadContentAsync(payload,id,current.AccessToken,deadline.Token); }
            catch (UnsupportedMailException ex) { notes.Add("Message "+id+": "+ex.Message+" Review manually in Gmail; no partial message imported."); continue; }
            if(plain is null){notes.Add("Message "+id+": no supported UTF-8 body or readable attachment text. It was excluded; review it manually in Gmail.");continue;}
            var subject=Subject(payload);var original=(subject.Length>0?"Subject: "+subject+"\n\n":"")+plain;
            if(original.Trim().Length is <20 or >6000 || original.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')))
            {notes.Add("Message "+id+": text is outside 20–6,000 characters or contains unsupported control characters. It was excluded without truncation.");continue;}
            var language=original.Any(c=>c is >= '\u0C80' and <= '\u0CFF')?"Kannada":"English";
            var source="https://mail.google.com/mail/?authuser="+Uri.EscapeDataString(current.Mailbox)+"#all/"+id;
            var row=string.Join(",",new[]{"Email","gmail:"+mailboxKey+":"+id,language,source,original}.Select(Csv));
            if(rows.Sum(r=>r.Length+1)+row.Length+60>InboxImportService.MaximumCharacters){notes.Add("Message "+id+": this batch reached its character limit; it was excluded. Use a smaller label batch.");continue;}
            rows.Add(row);
        }
        if(settings.Read(owner).Version!=current.Version)throw new InvalidOperationException("Gmail settings changed during preview. No messages were saved; preview again.");
        if(rows.Count==0)return new(null,notes);
        var csv="channel,external_id,language,source_url,message\n"+string.Join("\n",rows);
        return new(await imports.PreviewAsync(owner,actor,csv,cancellation),notes);
    }
    private static string Csv(string text)=>"\""+text.Replace("\"","\"\"")+"\"";
    public async Task<AutomationMailBatch> ReadInboxAsync(Guid owner,IReadOnlySet<string> seen,CancellationToken cancellation=default,Func<bool>? continueReading=null)
    {
        var current=settings.Read(owner);if(!current.Connected)throw new InvalidOperationException("Connect Gmail before enabling Inbox automation.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(90));
        if(current.AccessExpiresUtc<=clock.GetUtcNow().AddSeconds(60))
        {
            using var refreshed=await Token(new(){["client_id"]=current.ClientId,["client_secret"]=current.ClientSecret,["refresh_token"]=current.RefreshToken,["grant_type"]="refresh_token"},deadline.Token);
            var token=Credential(refreshed.RootElement,"access_token");var expires=Expiry(refreshed.RootElement);
            current=settings.Update(owner,value=>value.Version==current.Version&&value.Connected?value with{AccessToken=token,AccessExpiresUtc=expires}:throw new InvalidOperationException("Gmail settings changed. Retry the connection."));
        }
        var messages=new List<AutomationMail>();var unsupported=new List<string>();var retryable=0;var scanned=0;var page="";
        var mailboxKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Mailbox)))[..24];
        for(var number=0;number<5&&messages.Count+unsupported.Count+retryable<20;number++)
        {
            if(continueReading?.Invoke()==false||deadline.IsCancellationRequested)break;
            using var list=await Get("messages?maxResults=20&q="+Uri.EscapeDataString("in:inbox -in:spam -in:trash")+(page.Length==0?"":"&pageToken="+Uri.EscapeDataString(page)),current.AccessToken,deadline.Token);
            if(!list.RootElement.TryGetProperty("messages",out var entries))break;
            foreach(var entry in entries.EnumerateArray().Take(20))
            {
                if(continueReading?.Invoke()==false||deadline.IsCancellationRequested)break;
                if(messages.Count+unsupported.Count+retryable>=20)break;
                scanned++;var id=entry.GetProperty("id").GetString()??"";
                if(!Regex.IsMatch(id,@"^[A-Za-z0-9_-]{1,100}$"))throw new InvalidOperationException("Gmail returned an unsupported message ID.");
                var external="gmail:"+mailboxKey+":"+id;if(seen.Contains(external))continue;
                JsonDocument message;
                try{message=await Get("messages/"+id+"?format=full",current.AccessToken,deadline.Token);}
                catch(OversizedGmailContentException){unsupported.Add(external);continue;}
                using var messageLifetime=message;
                if(message.RootElement.GetProperty("id").GetString()!=id)throw new InvalidOperationException("Gmail returned a different message than requested.");
                if(!message.RootElement.TryGetProperty("labelIds",out var labels)||!labels.EnumerateArray().Any(l=>l.GetString()=="INBOX")||labels.EnumerateArray().Any(l=>l.GetString() is "SPAM" or "TRASH")){unsupported.Add(external);continue;}
                var payload=message.RootElement.GetProperty("payload");string? plain;
                try { plain=await ReadContentAsync(payload,id,current.AccessToken,deadline.Token,true); }
                catch (UnsupportedMailException) { unsupported.Add(external); continue; }
                catch (InvalidOperationException) { retryable++; continue; }
                var subject=Subject(payload);
                var original=(subject.Length>0?"Subject: "+subject+"\n\n":"")+plain;
                if(plain is null||original.Trim().Length is <20 or >6000||original.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t'))){unsupported.Add(external);continue;}
                messages.Add(new(external,original.Trim(),original.Any(c=>c is >= '\u0C80' and <= '\u0CFF')?"Kannada":"English","https://mail.google.com/mail/?authuser="+Uri.EscapeDataString(current.Mailbox)+"#all/"+id));
            }
            if(!list.RootElement.TryGetProperty("nextPageToken",out var next))break;
            page=next.GetString()??"";if(page.Length is 0 or >2048||page.Any(char.IsControl))break;
        }
        if(settings.Read(owner).Version!=current.Version)throw new InvalidOperationException("Gmail settings changed during collection. Retry with the current connection.");
        return new(messages,unsupported,scanned,retryable);
    }
    private static string Subject(JsonElement payload)
    {
        if(!payload.TryGetProperty("headers",out var headers))return "";
        var subject=headers.EnumerateArray().FirstOrDefault(h=>string.Equals(h.GetProperty("name").GetString(),"Subject",StringComparison.OrdinalIgnoreCase));
        if(subject.ValueKind==JsonValueKind.Undefined)return "";
        return (subject.GetProperty("value").GetString()??"").Trim();
    }
    private DateTimeOffset Expiry(JsonElement root)
    {
        if(!root.TryGetProperty("expires_in",out var value)||!value.TryGetInt32(out var seconds)||seconds is <1 or >86400)throw new InvalidOperationException("Google returned an invalid token expiry. Reconnect Gmail.");
        return clock.GetUtcNow().AddSeconds(seconds);
    }
    private static string Credential(JsonElement root,string field)
    {
        var value=root.GetProperty(field).GetString()??"";
        if(value.Length is <10 or >8192 || value.Any(c=>c<33||c>126))throw new InvalidOperationException("Google did not return reusable credentials. Connect Gmail again with offline permission.");
        return value;
    }
    private async Task<JsonDocument> Token(Dictionary<string,string> fields,CancellationToken cancellation)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"https://oauth2.googleapis.com/token"){Content=new FormUrlEncodedContent(fields)};return await Send(request,cancellation);}
    private async Task<JsonDocument> Get(string path,string token,CancellationToken cancellation)
    {using var request=new HttpRequestMessage(HttpMethod.Get,"https://gmail.googleapis.com/gmail/v1/users/me/"+path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);return await Send(request,cancellation,path.StartsWith("messages/",StringComparison.Ordinal)?12*1024*1024:128_000);}
    private async Task<JsonDocument> Send(HttpRequestMessage request,CancellationToken cancellation,int maximumBytes=128_000)
    {
        try
        {
            using var response=await clients.CreateClient("Gmail").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Google could not complete this request. Check Gmail API access, reconnect if needed, and retry. No messages were imported.");
            if(response.Content.Headers.ContentLength>maximumBytes)throw new OversizedGmailContentException();
            await using var stream=await response.Content.ReadAsStreamAsync(cancellation);using var memory=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer,cancellation))>0){if(memory.Length+count>maximumBytes)throw new OversizedGmailContentException();await memory.WriteAsync(buffer.AsMemory(0,count),cancellation);}
            return JsonDocument.Parse(memory.ToArray());
        }
        catch(Exception ex)when(ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {throw new InvalidOperationException("Gmail is unavailable or returned an unreadable response. Check the connection and retry; no messages were imported.");}
    }
}
