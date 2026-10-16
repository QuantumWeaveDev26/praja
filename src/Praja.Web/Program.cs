using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praja.Web.Data;
using Praja.Web.Services;

if (args is ["--extract-pdf-worker"]) { await PdfIntakeWorker.RunAsync(); return; }
var recoverPassword = args is ["--reset-officer-password"];
var builder = WebApplication.CreateBuilder(recoverPassword ? [] : args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys"))).SetApplicationName("Praja.Workspace");
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddDbContext<WorkspaceDb>(options =>
{
    var connection = builder.Configuration.GetConnectionString("Cases");
    if (builder.Configuration["Database:Provider"] == "SqlServer")
        options.UseSqlServer(connection ?? throw new InvalidOperationException("Supply ConnectionStrings__Cases for SQL Server."));
    else if (local)
        options.UseSqlite(connection ?? $"Data Source={Path.Combine(dataDirectory, "workspace.db")}");
    else throw new InvalidOperationException("Production requires Database__Provider=SqlServer and a managed database connection.");
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Account/Login"; options.Cookie.Name = "Praja.Session"; options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(4); options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = context.HttpContext.RequestServices.GetRequiredService<WorkspaceDb>();
        var user = Guid.TryParse(id, out var owner) ? await db.Operators.AsNoTracking().SingleOrDefaultAsync(u => u.Id == owner) : null;
        if (user is null || user.IsDisabled || context.Principal?.FindFirstValue("Praja.CredentialStamp") != WorkspaceIdentity.CredentialStamp(user))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode=403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Citizen");
    options.Conventions.AllowAnonymousToPage("/Track");
    options.Conventions.AllowAnonymousToPage("/Submit");
    options.Conventions.AllowAnonymousToPage("/OfflineSync");
    options.Conventions.AllowAnonymousToPage("/CitizenVoice");
    options.Conventions.AllowAnonymousToPage("/CitizenLogin");
    options.Conventions.AllowAnonymousToPage("/Transparency");
    options.Conventions.AllowAnonymousToPage("/PublicMap");
    options.Conventions.AllowAnonymousToPage("/PublicHistory");
    options.Conventions.AllowAnonymousToPage("/Community");
});
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
{
    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
}))
    .AddPolicy("password-change", context => HttpMethods.IsPost(context.Request.Method) ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) : RateLimitPartition.GetNoLimiter("password-page"))
    .AddPolicy("citizen", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("citizen-voice", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("community", context => HttpMethods.IsPost(context.Request.Method) ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }) : RateLimitPartition.GetNoLimiter("community-page"))
    .AddPolicy("tracking", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("intake", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("ai-settings", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("evidence", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("inbox", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.Configure<RateLimiterOptions>(options => { options.RejectionStatusCode=429; options.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(context => context.Request.Path.StartsWithSegments("/CitizenVoice") ? RateLimitPartition.GetFixedWindowLimiter("public-speech", _ => new FixedWindowRateLimiterOptions {PermitLimit=60,Window=TimeSpan.FromHours(1),QueueLimit=0}) : RateLimitPartition.GetNoLimiter("other")); });
builder.Services.AddHttpClient("Ai", client => client.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("Gmail",client=>client.Timeout=TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddSingleton<GmailConfigurationStore>();
builder.Services.AddHttpClient("X", client => client.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddSingleton<XConfigurationStore>();
builder.Services.AddScoped<XCollectionService>();
builder.Services.AddScoped<GmailService>();
builder.Services.AddScoped<DraftingService>(); builder.Services.AddScoped<CaseService>();
builder.Services.AddScoped<IntakeReviewService>();
builder.Services.AddScoped<InboxService>();
builder.Services.AddScoped<InboxImportService>();
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddHostedService<IntakeAutomationWorker>();
builder.Services.AddScoped<MediaIntakeService>();
builder.Services.AddScoped<EvidenceService>();
builder.Services.AddSingleton<AiConfigurationStore>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<CitizenAccessService>();
builder.Services.AddScoped<CaseBriefService>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHostedService<DeadlineWorker>();
builder.Services.AddScoped<PublicComplaintService>();
builder.Services.AddScoped<PublicMapService>();
builder.Services.AddScoped<CommunityFeedbackService>();
builder.Services.AddSingleton(TimeProvider.System);
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkspaceDb>();
    if (recoverPassword)
    {
        try
        {
            if (!local || !db.Database.IsSqlite() || !Console.IsInputRedirected)
                throw new InvalidOperationException("Use reset-password.ps1 for local pilot recovery.");
            var databasePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                throw new InvalidOperationException("The existing local workspace database was not found. No account was changed.");
            Console.InputEncoding = System.Text.Encoding.UTF8;
            var newPassword = await Console.In.ReadLineAsync() ?? "";
            var email = await LocalPasswordRecovery.ResetAsync(db, builder.Environment, newPassword, Path.Combine(dataDirectory, "backups"));
            Console.WriteLine($"Password reset for {email}. Sign in with your new password. Your cases were preserved.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or DbUpdateException)
        {
            Console.Error.WriteLine("Password reset did not complete. Check that the local database is available and your password has 14–128 characters.");
            Environment.ExitCode = 1;
        }
        return;
    }
    await WorkspaceSchema.InitializeAsync(db,builder.Configuration["Database:BackupDirectory"] ?? Path.Combine(dataDirectory,"backups"));
    if (!local && !await db.Operators.AnyAsync())
    {
        var password = builder.Configuration["Bootstrap:Password"] ?? "";
        var email = builder.Configuration["Bootstrap:Email"] ?? "";
        if (password.Length < 14 || !email.Contains('@')) throw new InvalidOperationException("Provision the initial officer using Bootstrap__Email and a strong Bootstrap__Password through deployment secrets.");
        var user = new Operator { Name = "Workspace officer", Email = email.Trim().ToLowerInvariant() };
        user.PasswordHash = WorkspaceIdentity.Hasher.HashPassword(user, password);
        db.Operators.Add(user); await db.SaveChangesAsync();
    }
}
app.UseExceptionHandler("/Error");
if (!local) { app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context, next) =>
{
    var publicMap = context.Request.Path.Equals("/PublicMap", StringComparison.OrdinalIgnoreCase);
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:" + (publicMap ? " https://tile.openstreetmap.org" : "") + "; media-src 'self' blob:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = publicMap ? "strict-origin-when-cross-origin" : "no-referrer";
    context.Response.Headers["Permissions-Policy"] = context.Request.Path.StartsWithSegments("/Submit")
        ? "camera=(self), microphone=(self), geolocation=(self)"
        : (context.Request.Path.StartsWithSegments("/Work/Case") || publicMap || context.Request.Path.StartsWithSegments("/Community"))
            ? "camera=(), microphone=(), geolocation=(self)"
            : context.Request.Path.StartsWithSegments("/Grievances/New")
                ? "camera=(), microphone=(self), geolocation=()"
                : "camera=(), microphone=(), geolocation=()";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    await next();
});
app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication();
app.Use(async (context,next)=>{if(context.Request.Path=="/" && context.Request.Method=="GET" && context.User.Identity?.IsAuthenticated!=true){context.Response.Redirect("/Submit");return;}await next();});
app.Use(async (context,next)=>
{
    if(context.User.Identity?.IsAuthenticated==true)
    {
        var db=context.RequestServices.GetRequiredService<WorkspaceDb>();
        var required=await db.Operators.AnyAsync(o=>o.Id==context.User.OwnerId()&&o.MustChangePassword);
        var path=(context.Request.Path.Value??"").TrimEnd('/');
        if(required&&!new[]{"/Account/ChangePassword","/Account/Logout","/Error"}.Contains(path,StringComparer.OrdinalIgnoreCase))
        {
            if(HttpMethods.IsGet(context.Request.Method)&&!context.Request.Path.StartsWithSegments("/api"))context.Response.Redirect("/Account/ChangePassword");
            else context.Response.StatusCode=403;
            return;
        }
    }
    await next();
});
app.UseAuthorization();
app.Use(async (context,next)=>
{
    if(context.User.Identity?.IsAuthenticated==true)
    {
        var role=await context.RequestServices.GetRequiredService<WorkflowService>().RoleAsync(context.User.OwnerId());
        var path=context.Request.Path;
        if(role.Role is "Department" or "Reviewer" or "Contractor" && !path.StartsWithSegments("/Work") && !path.StartsWithSegments("/Track") && !path.StartsWithSegments("/Submit") && !path.StartsWithSegments("/Citizen") && !path.StartsWithSegments("/CitizenVoice") && !path.StartsWithSegments("/CitizenLogin") && !path.StartsWithSegments("/Transparency") && !path.StartsWithSegments("/PublicMap") && !path.StartsWithSegments("/PublicHistory") && !path.StartsWithSegments("/Community") && !path.StartsWithSegments("/Account") && !path.StartsWithSegments("/Error"))
        { if(context.Request.Method=="GET")context.Response.Redirect("/Work");else context.Response.StatusCode=403;return; }
        if(role.Role=="Intake"&&(path.StartsWithSegments("/AiSettings")||path.StartsWithSegments("/GmailSettings")||path.StartsWithSegments("/XSettings")||path.StartsWithSegments("/Connections")||path.StartsWithSegments("/Automation")))
        {context.Response.StatusCode=403;return;}
    }
    await next();
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/api/cases", async (HttpContext context, WorkspaceDb db) =>
{
    var owner = context.User.OwnerId();
    var items = await db.Grievances.Where(g => g.OwnerId == owner).OrderByDescending(g => g.CreatedUtc)
        .Select(g => new { g.Id, g.Reference, g.Title, g.Status, g.Department, g.IsSample, g.CreatedUtc, g.Version }).Take(200).ToListAsync();
    return Results.Ok(items);
}).RequireAuthorization();
app.MapGet("/api/cases/{id:guid}", async (Guid id, HttpContext context, WorkspaceDb db) =>
{
    var owner = context.User.OwnerId();
    var item = await db.Grievances.AsNoTracking().Where(g => g.Id == id && g.OwnerId == owner)
        .Select(g => new { g.Id, g.Reference, g.Title, g.OriginalText, g.DraftText, g.Department, g.Status, g.Version, g.IsSample, integrationStatus = "Not connected" }).SingleOrDefaultAsync();
    return item is null ? Results.NotFound() : Results.Ok(item);
}).RequireAuthorization();
app.MapRazorPages();
app.Run();
public partial class Program;
