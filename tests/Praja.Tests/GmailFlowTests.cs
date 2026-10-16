using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Praja.Web.Data;
using Praja.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Praja.Tests;

public partial class GmailFlowTests
{
    private const string ClientId = "qa-client.apps.googleusercontent.com";
    private const string Secret = "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";
    private const string Mailbox = "team.quantumweaveai@gmail.com";
    [Fact]
    public async Task ProtectedCredentialFilesRejectWorkspaceCopyAndTampering()
    {
        using var first=new TestApp();using var client=first.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        using var second=new TestApp();using var other=second.CreateClient(new(){AllowAutoRedirect=false});await Setup(other);await Save(other);
        var source=Directory.GetFiles(first.SettingsDirectory,"gmail-*.protected").Single();var destination=Directory.GetFiles(second.SettingsDirectory,"gmail-*.protected").Single();
        File.Copy(source,destination,true);
        using(var scope=second.Services.CreateScope()){var owner=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();Assert.False(scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>().Read(owner.Id).Configured);}
        await File.WriteAllTextAsync(source,"tampered");
        using(var scope=first.Services.CreateScope()){var owner=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();Assert.False(scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>().Read(owner.Id).Configured);}
    }

    [Fact]
    public async Task ExpiredOAuthStateCannotReachGoogle()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        using var scope=app.Services.CreateScope();var owner=await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().Operators.SingleAsync();var clock=new MutableClock();
        var service=new GmailService(scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>(),scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(),clock,scope.ServiceProvider.GetRequiredService<InboxImportService>());
        var started=service.Start(owner.Id,"http://localhost/Connections/GmailCallback",true);var state=QueryHelpers.ParseQuery(new Uri(started).Query)["state"].ToString();
        clock.Now=clock.Now.AddMinutes(10);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.CompleteAsync(owner.Id,state,"synthetic-code",null));Assert.Equal(0,provider.Calls);
    }

    [Fact]
    public async Task SystemLabelCannotReadInboxBodiesAndRelabelledMessagesAreExcluded()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        provider.LabelType="system";
        var blocked=await Post(client,"/GmailSettings?handler=Preview",new());Assert.Contains("Built-in labels",await blocked.Content.ReadAsStringAsync());Assert.Equal(0,provider.MessageReads);
        provider.LabelType="user";provider.MessageLabel="INBOX";
        var changed=await Post(client,"/GmailSettings?handler=Preview",new());Assert.Contains("complaint label changed",await changed.Content.ReadAsStringAsync());
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("Other-Complaints")]
    [InlineData("praja-complaints")]
    [InlineData(null)]
    public async Task MissingComplaintLabelNamesExpectedLabelAndMailboxWithoutReadingBodies(string? availableLabel)
    {
        var provider=new GoogleReply{LabelName=availableLabel};using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var blocked=await Post(client,"/GmailSettings?handler=Preview",new());var html=WebUtility.HtmlDecode(await blocked.Content.ReadAsStringAsync());
        Assert.Contains("custom label \"Praja-Complaints\" was not found in \""+Mailbox+"\"",html);
        Assert.Contains("case-sensitive",html);Assert.DoesNotContain(Secret,html);Assert.Equal(0,provider.MessageReads);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task OlderCallbackCannotOverwriteNewerConnectionAttempt()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        var first=await Connect(client);var state=QueryHelpers.ParseQuery(first.Headers.Location!.Query)["state"].ToString();
        provider.OnProfile=()=>{using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=db.Operators.Single();scope.ServiceProvider.GetRequiredService<GmailService>().Start(owner.Id,"http://localhost/Connections/GmailCallback",true);};
        await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state="+Uri.EscapeDataString(state));
        using var check=app.Services.CreateScope();var savedDb=check.ServiceProvider.GetRequiredService<WorkspaceDb>();var account=await savedDb.Operators.SingleAsync();
        Assert.False(check.ServiceProvider.GetRequiredService<GmailConfigurationStore>().Read(account.Id).Connected);
        Assert.Contains("Configuration changed",await client.GetStringAsync("/GmailSettings"));
    }

    [Fact]
    public async Task CopiedCredentialsAndOAuthStateFromAnotherWorkspaceAreRejected()
    {
        var provider=new GoogleReply();using var first=new TestApp(provider);using var client=first.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        var started=await Connect(client);var state=QueryHelpers.ParseQuery(started.Headers.Location!.Query)["state"].ToString();
        using var second=new TestApp(provider);using var other=second.CreateClient(new(){AllowAutoRedirect=false});await Setup(other);
        await other.GetAsync("/Connections/GmailCallback?code=synthetic-code&state="+Uri.EscapeDataString(state));Assert.Equal(0,provider.Calls);
        using var scope=second.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();var store=scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>();
        Assert.False(store.Read(owner.Id).Configured);
        Assert.DoesNotContain(Secret,await other.GetStringAsync("/GmailSettings"));
    }

    [Fact]
    public async Task RefreshAndFailedFetchNeverSaveMessages()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();scope.ServiceProvider.GetRequiredService<GmailConfigurationStore>().Update(owner.Id,c=>c with{AccessExpiresUtc=DateTimeOffset.UtcNow.AddSeconds(-1)});}
        var refreshed=await Post(client,"/GmailSettings?handler=Preview",new());Assert.Contains("No water supply",await refreshed.Content.ReadAsStringAsync());Assert.Contains("grant_type=refresh_token",provider.TokenBody);
        provider.FailFetch=true;
        var failed=await Post(client,"/GmailSettings?handler=Preview",new());Assert.Contains("No messages were imported",await failed.Content.ReadAsStringAsync());
        using var check=app.Services.CreateScope();Assert.Empty(await check.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task FailedImportNeverEchoesPostedClientSecretAndMissingPermissionDoesNotSave()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var preview=await Post(client,"/GmailSettings?handler=Preview",new());var token=Hidden(await preview.Content.ReadAsStringAsync(),"PreviewToken");
        var failed=await Post(client,"/GmailSettings?handler=Import",new(){["PreviewToken"]=token,["Input.ClientSecret"]=Secret});var html=await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret,html);Assert.Contains("Confirm permission",html);
        Assert.DoesNotContain("<li>The ClientId field is required.</li>",html);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ImportWithoutPreviewRejectsReceiptWithoutUnrelatedSettingsErrors()
    {
        using var app=new TestApp();using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        var failed=await Post(client,"/GmailSettings?handler=Import",new(){["RetainPermission"]="true"});var html=await failed.Content.ReadAsStringAsync();
        Assert.Contains("Preview these messages again",html);
        Assert.DoesNotContain("<li>The ClientId field is required.</li>",html);
        Assert.DoesNotContain("<li>The PreviewToken field is required.</li>",html);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ExpiredPendingStateAndChangedClientCannotConnect()
    {
        var provider=new GoogleReply();using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);
        var pending=await Connect(client);var state=QueryHelpers.ParseQuery(pending.Headers.Location!.Query)["state"].ToString();
        await Save(client);
        await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state="+Uri.EscapeDataString(state));Assert.Equal(0,provider.Calls);
        var newClient=await Post(client,"/GmailSettings?handler=Save",new(){["Input.ClientId"]="different.apps.googleusercontent.com",["Input.ClientSecret"]="",["Input.Mailbox"]=Mailbox,["Input.LabelName"]="Praja-Complaints"});
        Assert.Contains("matching secret",await newClient.Content.ReadAsStringAsync());
    }

    [Fact]
    public void CallbackRejectsUntrustedHosts()
    {
        var request=new DefaultHttpContext().Request;request.Scheme="http";request.Host=new("attacker.example.test");
        Assert.Throws<InvalidOperationException>(()=>GmailService.CallbackUrl(request));
        request.Host=new("127.0.0.1:5080");Assert.Equal("http://127.0.0.1:5080/Connections/GmailCallback",GmailService.CallbackUrl(request));
    }

    [Fact]
    public async Task SettingsArePrivateEncryptedAndSecretNeverEchoes()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/GmailSettings")).StatusCode);
        await Setup(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/GmailSettings?handler=Save", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        var saved = await Save(client); var html = await saved.Content.ReadAsStringAsync();
        Assert.Contains("Configuration saved", html); Assert.DoesNotContain(Secret, html);
        Assert.DoesNotContain("validation-summary-errors", html);
        Assert.Contains("/Connections/GmailCallback", html);
        var file = Directory.GetFiles(app.SettingsDirectory, "gmail-*.protected").Single();
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(file)); Assert.DoesNotContain(Mailbox, await File.ReadAllTextAsync(file));
        var blank = await Save(client, ""); var blankHtml = await blank.Content.ReadAsStringAsync();
        Assert.Contains("Configuration saved", blankHtml); Assert.DoesNotContain("validation-summary-errors", blankHtml);
        var invalid = await Save(client, "bad secret with spaces"); Assert.DoesNotContain("bad secret with spaces", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConnectCallbackPreviewAndRetryImportCompleteWithoutRegisteringCases()
    {
        var provider = new GoogleReply(); using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        var connected = await Connect(client);
        Assert.Equal(HttpStatusCode.Redirect, connected.StatusCode); Assert.StartsWith("https://accounts.google.com/", connected.Headers.Location!.ToString());
        var query = QueryHelpers.ParseQuery(connected.Headers.Location.Query);
        Assert.Equal("https://www.googleapis.com/auth/gmail.readonly", query["scope"].ToString());
        Assert.Equal("http://localhost/Connections/GmailCallback", query["redirect_uri"].ToString());
        var callback = await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/GmailSettings", callback.Headers.Location!.ToString());
        var page = await client.GetStringAsync("/GmailSettings"); Assert.Contains("Connected", page); Assert.DoesNotContain("synthetic-access-token", page); Assert.DoesNotContain(Secret, page);
        var preview = await Post(client, "/GmailSettings?handler=Preview", new());
        var html = await preview.Content.ReadAsStringAsync(); Assert.Contains("No water supply", html); Assert.Contains("Water supply", html);
        Assert.Contains("-in:spam", provider.SearchQuery); Assert.Equal("Label_Complaints",provider.LabelIds);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>(); Assert.Empty(await db.InboxMessages.ToListAsync());
        var receipt = Hidden(html, "PreviewToken"); Assert.NotEmpty(receipt);
        var imported = await Post(client, "/GmailSettings?handler=Import", new() { ["PreviewToken"] = receipt, ["RetainPermission"] = "true" });
        Assert.Contains("1 new", await imported.Content.ReadAsStringAsync());
        var retry = await Post(client, "/GmailSettings?handler=Import", new() { ["PreviewToken"] = receipt, ["RetainPermission"] = "true" });
        Assert.Contains("1 already saved", await retry.Content.ReadAsStringAsync());
        var item = await db.InboxMessages.SingleAsync(); Assert.Equal("Email", item.Channel); Assert.Equal("Pending", item.State);
        Assert.Empty(await db.Grievances.ToListAsync()); Assert.Equal(0, provider.WriteCalls);
        Assert.Contains("code_verifier", provider.TokenBody);
        Assert.DoesNotContain("synthetic-refresh-token", await File.ReadAllTextAsync(Directory.GetFiles(app.SettingsDirectory,"gmail-*.protected").Single()));
    }

    [Fact]
    public async Task InvalidStateMismatchMailboxAndDeniedConsentNeverConnect()
    {
        var provider = new GoogleReply { ProfileEmail = "someone-else@gmail.com" };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client);
        var blocked = await Post(client, "/GmailSettings?handler=Connect", new()); Assert.Equal(HttpStatusCode.OK, blocked.StatusCode); Assert.Equal(0, provider.Calls);
        var invalid = await client.GetAsync("/Connections/GmailCallback?code=secret-code&state=bad-state");
        Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode); Assert.Equal(0, provider.Calls);
        var response = await Connect(client); var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state=" + Uri.EscapeDataString(state));
        Assert.Contains("configured mailbox", await client.GetStringAsync("/GmailSettings"));
        var after = provider.Calls;
        await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state=" + Uri.EscapeDataString(state)); Assert.Equal(after, provider.Calls);
        var preview = await Post(client, "/GmailSettings?handler=Preview", new()); Assert.Contains("Connect Gmail", await preview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HtmlOnlyIsConvertedAndOversizedEmailsAreExcludedWithVisibleReasons()
    {
        var provider = new GoogleReply { MimeType = "text/html" };
        using var app = new TestApp(provider); using var client = app.CreateClient(new() { AllowAutoRedirect = false }); await Setup(client); await Save(client); await FinishConnect(client);
        provider.MessageText="<head><style>secret-style</style></head><script>secret-script</script><p>No water supply in Ward 12, Mysuru.</p><p>Please restore &amp; repair.</p><img src='https://tracker.invalid/pixel'>";
        var htmlOnly = await Post(client,"/GmailSettings?handler=Preview",new()); var html=await htmlOnly.Content.ReadAsStringAsync();Assert.Contains("No water supply",html);Assert.DoesNotContain("secret-script",html);Assert.DoesNotContain("secret-style",html);Assert.DoesNotContain("tracker.invalid",html);
        provider.MimeType = "text/plain"; provider.MessageText = new string('x',6100);
        var longText = await Post(client,"/GmailSettings?handler=Preview",new()); Assert.Contains("6,000",WebUtility.HtmlDecode(await longText.Content.ReadAsStringAsync()));
        using var scope=app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DisconnectStopsFetchingAndCallbackCannotRestoreOldConnection()
    {
        var provider = new GoogleReply(); using var app=new TestApp(provider); using var client=app.CreateClient(new(){AllowAutoRedirect=false}); await Setup(client); await Save(client); await FinishConnect(client);
        var pending=await Connect(client); var state=QueryHelpers.ParseQuery(pending.Headers.Location!.Query)["state"].ToString();
        var disconnect=await Post(client,"/GmailSettings?handler=Disconnect",new()); Assert.Contains("Disconnected",await disconnect.Content.ReadAsStringAsync());
        var calls=provider.Calls; await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state="+Uri.EscapeDataString(state)); Assert.Equal(calls,provider.Calls);
        var preview=await Post(client,"/GmailSettings?handler=Preview",new()); Assert.Contains("Connect Gmail",await preview.Content.ReadAsStringAsync()); Assert.Equal(calls,provider.Calls);
    }

    private static object Multipart(string filename="petition.txt",int size=100,string body="<p>Please inspect the attached petition.</p>")=>new{mimeType="multipart/mixed",parts=new object[]{
        new{mimeType="text/html",body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(body))}},
        new{mimeType="application/octet-stream",filename,body=new{attachmentId="part1",size}}}};

    [Fact]
    public async Task PlainAlternativeWinsAndInlineKannadaAttachmentRemainsSourceText()
    {
        var provider=new GoogleReply{CustomPayload=new{mimeType="multipart/mixed",parts=new object[]{
            new{mimeType="multipart/alternative",parts=new object[]{
                new{mimeType="text/html",body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("<p>Alternative must not duplicate</p>"))}},
                new{mimeType="text/plain",body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("No water supply in Ward 12, Mysuru. Please restore supply."))}}}},
            new{mimeType="text/plain",filename="petition.txt",body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("ಮೈಸೂರು ವಾರ್ಡ್ 12 ನೀರು ಇಲ್ಲ"))}}}}};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var response=await Post(client,"/GmailSettings?handler=Preview",new());var html=WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("No water supply",html);Assert.Contains("ಮೈಸೂರು",html);Assert.DoesNotContain("Alternative must not duplicate",html);Assert.Equal(0,provider.AttachmentReads);
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("count")]
    [InlineData("combined")]
    public async Task MalformedOrExcessiveAttachmentContentCannotProducePartialPreview(string failure)
    {
        var provider=new GoogleReply{CustomPayload=Multipart()};
        if(failure=="encoding")provider.AttachmentBytes=[255,254,255];
        if(failure=="combined")provider.AttachmentBytes=Encoding.UTF8.GetBytes(new string('x',6000));
        if(failure=="count")provider.CustomPayload=new{mimeType="multipart/mixed",parts=Enumerable.Range(0,4).Select(i=>new{mimeType="text/plain",filename="file.txt",body=new{attachmentId="part1",size=100}}).ToArray()};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var response=await Post(client,"/GmailSettings?handler=Preview",new());var html=await response.Content.ReadAsStringAsync();Assert.Contains("no partial message imported",html);Assert.DoesNotContain("Confirm email import",html);
        if(failure=="count")Assert.Equal(0,provider.AttachmentReads);
    }

    [Fact]
    public async Task HtmlAndAttachmentAutomationRetriesThenPreparesAndRegistersExactlyOnce()
    {
        var provider=new GoogleReply{CustomPayload=Multipart(),MessageLabel="INBOX",FailAttachment=true};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        using var scope=app.Services.CreateScope();var sp=scope.ServiceProvider;var db=sp.GetRequiredService<WorkspaceDb>();var owner=await db.Operators.SingleAsync();
        Assert.Empty(await db.InboxMessages.ToListAsync());Assert.Empty(sp.GetRequiredService<AutomationStore>().Read(owner.Id).Seen);
        provider.FailAttachment=false;Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Automation?handler=Run",new())).StatusCode);
        Assert.True(await db.InboxMessages.AnyAsync(),sp.GetRequiredService<AutomationStore>().Read(owner.Id).Status+" Reads="+provider.AttachmentReads);
        var item=await db.InboxMessages.SingleAsync();Assert.Contains("[Attachment 1: local .txt text]",item.OriginalText);Assert.Contains("Ward 12",item.OriginalText);
        Assert.Single(sp.GetRequiredService<AutomationStore>().Read(owner.Id).Prepared);Assert.Empty(await db.Grievances.ToListAsync());
        var reads=provider.AttachmentReads;await Post(client,"/Automation?handler=Run",new());Assert.Equal(reads,provider.AttachmentReads);
        var approval=new Dictionary<string,string>{["Version"]="1",["Title"]="Water supply complaint",["DraftText"]="No water supply in Ward 12, Mysuru. Please restore supply.",["District"]="Mysuru",["Location"]="Ward 12",["Department"]="water",["CategoryCode"]="water_supply",["SubcategoryCode"]="no_supply",["Reviewed"]="true"};
        Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Automation/Review/"+item.Id+"?handler=Approve",approval)).StatusCode);
        var grievance=await db.Grievances.SingleAsync();Assert.Equal("Registered",grievance.Status);Assert.Contains("[Attachment 1:",grievance.OriginalText);
        Assert.Empty(await db.EvidenceAttachments.ToListAsync());Assert.Equal(0,provider.WriteCalls);
    }

    [Theory]
    [InlineData("petition.exe",100)]
    [InlineData("petition.txt",65537)]
    [InlineData("petition.pdf",100)]
    public async Task UnsupportedAttachmentExcludesEntireEmailWithoutPartialImport(string filename,int size)
    {
        var provider=new GoogleReply{CustomPayload=Multipart(filename,size,"<p>No water supply in Ward 12, Mysuru. Please restore supply.</p>")};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var response=await Post(client,"/GmailSettings?handler=Preview",new());var html=await response.Content.ReadAsStringAsync();Assert.Contains("no partial message imported",html);Assert.DoesNotContain("Confirm email import",html);
        using var scope=app.Services.CreateScope();Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
        if(filename.EndsWith(".exe")||size>65536)Assert.Equal(0,provider.AttachmentReads);
        provider.MessageLabel="INBOX";await Post(client,"/Automation?handler=Enable",new(){["Permission"]="true"});await Post(client,"/Automation?handler=Run",new());
        Assert.Contains("Review Gmail message abc123",await client.GetStringAsync("/Automation"));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task AttachmentOnlyTextPdfIsExtractedLocallyAndPreviewCanBeImportedOnce()
    {
        var builder=new UglyToad.PdfPig.Writer.PdfDocumentBuilder();var page=builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font=builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText("No water supply in Ward 12, Mysuru. Please restore supply.",12,new UglyToad.PdfPig.Core.PdfPoint(30,700),font);
        var provider=new GoogleReply{CustomPayload=Multipart("petition.pdf",100,""),AttachmentBytes=builder.Build()};
        using var app=new TestApp(provider);using var client=app.CreateClient(new(){AllowAutoRedirect=false});await Setup(client);await Save(client);await FinishConnect(client);
        var response=await Post(client,"/GmailSettings?handler=Preview",new());var html=await response.Content.ReadAsStringAsync();Assert.Contains("No water supply",html);Assert.Contains("local .pdf text",html);
        var receipt=Hidden(html,"PreviewToken");Assert.NotEmpty(receipt);
        for(var i=0;i<2;i++)await Post(client,"/GmailSettings?handler=Import",new(){["PreviewToken"]=receipt,["RetainPermission"]="true"});
        using var scope=app.Services.CreateScope();Assert.Single(await scope.ServiceProvider.GetRequiredService<WorkspaceDb>().InboxMessages.ToListAsync());Assert.Equal(0,provider.WriteCalls);
    }

    private static Task<HttpResponseMessage> Save(HttpClient client,string secret=Secret)=>Post(client,"/GmailSettings?handler=Save",new(){["Input.ClientId"]=ClientId,["Input.ClientSecret"]=secret,["Input.Mailbox"]=Mailbox,["Input.LabelName"]="Praja-Complaints"});
    private static Task<HttpResponseMessage> Connect(HttpClient client)=>Post(client,"/GmailSettings?handler=Connect",new(){["ConnectPermission"]="true"});
    private static async Task FinishConnect(HttpClient client){var result=await Connect(client);var state=QueryHelpers.ParseQuery(result.Headers.Location!.Query)["state"].ToString();Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/Connections/GmailCallback?code=synthetic-code&state="+Uri.EscapeDataString(state))).StatusCode);}
    private static string Hidden(string html,string name)=>WebUtility.HtmlDecode(Regex.Match(html,"name=\""+name+"\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static async Task Setup(HttpClient client)=>Assert.Equal(HttpStatusCode.Redirect,(await Post(client,"/Setup",new(){["Input.Name"]="Mail reviewer",["Input.Email"]="mail@example.test",["Input.Password"]="Gmail-Test-Password-72!"})).StatusCode);
    private static async Task<HttpResponseMessage> Post(HttpClient client,string path,Dictionary<string,string> fields){var page=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,page.StatusCode);fields["__RequestVerificationToken"]=Hidden(await page.Content.ReadAsStringAsync(),"__RequestVerificationToken");return await client.PostAsync(path,new FormUrlEncodedContent(fields));}
    private sealed class GoogleReply:HttpMessageHandler
    {
        public int Calls{get;private set;} public int WriteCalls{get;private set;} public string ProfileEmail{get;set;}=Mailbox;
        public string MimeType{get;set;}="text/plain"; public string MessageText{get;set;}="No water supply in Ward 12, Mysuru, for three days. Please restore supply.";
        public string TokenBody{get;private set;}=""; public string SearchQuery{get;private set;}="";
        public bool FailFetch{get;set;}
        public object? CustomPayload{get;set;} public byte[] AttachmentBytes{get;set;}=Encoding.UTF8.GetBytes("No water supply in Ward 12, Mysuru. Please restore supply.");
        public int AttachmentReads; public bool FailAttachment;
        public int PdfCalls, PdfPages = 1; public string? PdfFailure; public bool SecondTextMessage;
        public string LabelType{get;set;}="user";public string? LabelName{get;set;}="Praja-Complaints";public string MessageLabel{get;set;}="Label_Complaints";public string LabelIds{get;private set;}="";public int MessageReads{get;private set;}public Action? OnProfile{get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            Calls++;object payload;
            if (request.RequestUri!.Host == "provider.example.test")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellation);
                if (!body.Contains("file_data")) return new(HttpStatusCode.ServiceUnavailable);
                PdfCalls++;
                if (PdfFailure == "provider-error") return new(HttpStatusCode.ServiceUnavailable);
                var pages = Enumerable.Range(1, PdfFailure == "missing-page" ? 1 : PdfPages).Select(page => new { page, text = "No water supply in Ward 12, Mysuru, for three days. Please restore supply." }).ToArray();
                var result = new { choices = new[] { new { finish_reason = PdfFailure == "truncated" ? "length" : "stop", message = new { content = JsonSerializer.Serialize(new { pages }) } } } };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
            }
            if(FailFetch&&request.RequestUri!.Host=="gmail.googleapis.com")return new(HttpStatusCode.ServiceUnavailable);
            if(request.RequestUri!.Host=="oauth2.googleapis.com") {TokenBody=await request.Content!.ReadAsStringAsync(cancellation);payload=new{access_token="synthetic-access-token",refresh_token="synthetic-refresh-token",token_type="Bearer",expires_in=3600,scope="https://www.googleapis.com/auth/gmail.readonly"};}
            else if(request.RequestUri.AbsolutePath.EndsWith("/profile")){OnProfile?.Invoke();payload=new{emailAddress=ProfileEmail};}
            else if(request.RequestUri.AbsolutePath.EndsWith("/labels"))payload=new{labels=new[]{new{id="Label_Complaints",name=LabelName,type=LabelType}}.Where(label=>label.name is not null).ToArray()};
            else if(request.RequestUri.AbsolutePath.EndsWith("/messages")){var query=QueryHelpers.ParseQuery(request.RequestUri.Query);SearchQuery=query["q"].ToString();LabelIds=query.TryGetValue("labelIds",out var labelFilter)?labelFilter.ToString():"";payload=new{messages=(SecondTextMessage ? new[]{"abc123","def456"} : new[]{"abc123"}).Select(id=>new{id}).ToArray()};}
            else if(request.RequestUri.AbsolutePath.EndsWith("/attachments/part1")){AttachmentReads++;if(FailAttachment)return new(HttpStatusCode.ServiceUnavailable);payload=new{data=WebEncoders.Base64UrlEncode(AttachmentBytes),size=AttachmentBytes.Length};}
            else if(request.RequestUri.AbsolutePath.EndsWith("/messages/def456")){MessageReads++;payload=new{id="def456",labelIds=new[]{"INBOX"},payload=new{mimeType="text/plain",body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("Street lights have stopped working in Ward 9, Mysuru. Please repair the broken street lights."))}}};}
            else if(request.RequestUri.AbsolutePath.EndsWith("/messages/abc123")){MessageReads++;payload=new{id="abc123",labelIds=new[]{MessageLabel},payload=CustomPayload??new{mimeType=MimeType,headers=new[]{new{name="Subject",value="Water complaint"}},body=new{data=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(MessageText))}}};}
            else {if(request.RequestUri.Host=="gmail.googleapis.com")WriteCalls++;return new(HttpStatusCode.BadRequest);}
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class MutableClock:TimeProvider
    {
        public DateTimeOffset Now{get;set;}=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
