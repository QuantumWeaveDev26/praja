using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Praja.Web.Services;

namespace Praja.Web.Pages;
[RequestSizeLimit(256*1024)]
[EnableRateLimiting("ai-settings")]
public class GmailSettingsModel(GmailConfigurationStore settings,GmailService gmail,InboxImportService imports,IWebHostEnvironment environment):PageModel
{
    [BindProperty]public SettingsInput Input{get;set;}=new();
    [BindProperty]public bool ConnectPermission{get;set;}
    [BindProperty]public bool RetainPermission{get;set;}
    [BindProperty]public string? PreviewToken{get;set;}
    public bool Configured{get;private set;} public bool Connected{get;private set;}
    public string Callback{get;private set;}="";public string? Notice{get;private set;}
    public IReadOnlyList<ImportPreviewRow> Rows{get;private set;}=[]; public IReadOnlyList<string> Notes{get;private set;}=[];
    public IReadOnlyList<ImportedMessage>? Results{get;private set;}
    public class SettingsInput{public string ClientId{get;set;}="";public string? ClientSecret{get;set;}public string Mailbox{get;set;}="team.quantumweaveai@gmail.com";public string LabelName{get;set;}="Praja-Complaints";}
    public IActionResult OnGet(){if(!Allowed())return NotFound();Load();if(TempData["GmailNotice"]is string notice)Notice=notice;return Page();}
    public IActionResult OnPostSave()
    {
        if(!Allowed())return NotFound();
        try{settings.Save(User.OwnerId(),Input.ClientId??"",Input.ClientSecret,Input.Mailbox??"",Input.LabelName??"");Notice="Configuration saved. Connect Gmail next. Saved changes require a fresh connection.";}
        catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException){ModelState.AddModelError("","Configuration could not be saved. Check local file access and retry.");}
        Input.ClientSecret=null;ModelState.Remove("Input.ClientSecret");Load(preserveInputs:true);return Page();
    }
    public IActionResult OnPostConnect()
    {
        if(!Allowed())return NotFound();ModelState.Clear();Load();
        try{return Redirect(gmail.Start(User.OwnerId(),Callback,ConnectPermission));}
        catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);return Page();}
    }
    public async Task<IActionResult>OnPostPreviewAsync()
    {
        if(!Allowed())return NotFound();ModelState.Clear();Load();
        try{var result=await gmail.PreviewAsync(User.OwnerId(),User.Identity!.Name!,HttpContext.RequestAborted);Rows=result.Preview?.Rows??[];PreviewToken=result.Preview?.Token??"";Notes=result.Notes;}
        catch(Exception ex)when(ex is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or ArgumentException){ModelState.AddModelError("",ex is InvalidOperationException?ex.Message:"Gmail returned an unsupported response. No messages were saved.");}
        return Page();
    }
    public async Task<IActionResult>OnPostImportAsync()
    {
        if(!Allowed())return NotFound();ModelState.Clear();Load();
        try{Results=await imports.ImportAsync(User.OwnerId(),User.Identity!.Name!,PreviewToken??"",RetainPermission,HttpContext.RequestAborted);ModelState.Clear();PreviewToken="";}
        catch(InvalidOperationException ex){ModelState.AddModelError("",ex.Message);}
        catch(Microsoft.EntityFrameworkCore.DbUpdateException){ModelState.AddModelError("","The import was not saved. No partial batch was retained. Preview and retry.");}
        return Page();
    }
    public IActionResult OnPostDisconnect(){if(!Allowed())return NotFound();settings.Disconnect(User.OwnerId());ModelState.Clear();Load();Notice="Disconnected. Praja stopped fetching and removed its saved tokens. Existing inbox messages remain. You can also revoke Praja in your Google Account connections.";return Page();}
    private void Load(bool preserveInputs=false){ModelState.Remove("Input.ClientSecret");Input.ClientSecret=null;var current=settings.Read(User.OwnerId());Configured=current.Configured;Connected=current.Connected;Callback=GmailService.CallbackUrl(Request);if(!preserveInputs)Input=new(){ClientId=current.ClientId,Mailbox=current.Mailbox,LabelName=current.LabelName};}
    private bool Allowed()=>(environment.IsDevelopment()||environment.IsEnvironment("Testing"))&&(HttpContext.Connection.RemoteIpAddress is{}address?IPAddress.IsLoopback(address):environment.IsEnvironment("Testing"));
}
