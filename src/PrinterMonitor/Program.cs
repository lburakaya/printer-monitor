using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using PrinterMonitor;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
if (args.Length > 0) builder.Configuration.AddCommandLine(args);
builder.Services.AddWindowsService(x => x.ServiceName = "PrinterMonitor");
var settings = builder.Configuration.GetSection("Monitor").Get<MonitorOptions>() ?? new();
settings.Validate();
settings.DataDirectory = Path.GetFullPath(settings.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(settings.DataDirectory);
builder.Services.AddSingleton(settings);
var protection = builder.Services.AddDataProtection().SetApplicationName("PrinterMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton<PrinterStore>();
builder.Services.AddSingleton<NetworkPolicy>();
builder.Services.AddSingleton<IPrinterAdapter>(sp => settings.DemoMode ? new DemoPrinterAdapter() : new SnmpPrinterAdapter(settings, sp.GetRequiredService<NetworkPolicy>()));
builder.Services.AddSingleton<PollCoordinator>();
builder.Services.AddHostedService<PollingService>();
builder.Services.AddAntiforgery(x => { x.HeaderName = "X-CSRF-TOKEN"; x.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(x =>
{
    x.Cookie.Name = "PrinterMonitor.Session";
    x.Cookie.HttpOnly = true; x.Cookie.SameSite = SameSiteMode.Strict;
    x.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    x.ExpireTimeSpan = TimeSpan.FromHours(8);
    x.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(x =>
{
    x.RejectionStatusCode = 429;
    x.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    x.AddConcurrencyLimiter("poll", o => { o.PermitLimit = 2; o.QueueLimit = 0; });
});
builder.WebHost.ConfigureKestrel(x => x.Limits.MaxRequestBodySize = 16384);
var app = builder.Build();
var store = app.Services.GetRequiredService<PrinterStore>();
_ = app.Services.GetRequiredService<NetworkPolicy>(); // Validate CIDRs at startup.
if (settings.DemoMode) DemoPrinterAdapter.Seed(store);

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (settings.DemoMode && ctx.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
    { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Demo modu yalnızca bu bilgisayardan açılabilir." }); return; }
    try { await next(); }
    catch (ValidationException ex) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (BadHttpRequestException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Geçersiz istek." }); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        app.Logger.LogError("İstek başarısız: {Type}", ex.GetType().Name);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "İşlem tamamlanamadı. Servis kayıtlarını kontrol edin." }); }
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var publicPath = ctx.Request.Path == "/api/session" || ctx.Request.Path == "/api/login";
        if (!settings.DemoMode && !publicPath && ctx.User.Identity?.IsAuthenticated != true)
        { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Oturum açmanız gerekiyor." }); return; }
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Oturum doğrulaması başarısız. Sayfayı yenileyin." }); return; }
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/session", (HttpContext ctx, IAntiforgery csrf) => new
{
    demoMode = settings.DemoMode, authenticated = settings.DemoMode || ctx.User.Identity?.IsAuthenticated == true,
    csrfToken = csrf.GetAndStoreTokens(ctx).RequestToken,
    pollSeconds = settings.PollSeconds, lowThreshold = settings.LowThreshold
});
app.MapPost("/api/login", async (LoginInput input, HttpContext ctx) =>
{
    var given = SHA256.HashData(Encoding.UTF8.GetBytes(input.Password ?? ""));
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AdminPassword ?? ""));
    if (settings.DemoMode || !CryptographicOperations.FixedTimeEquals(given, expected)) return Results.Unauthorized();
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Yönetici")], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { message = "Oturum açıldı." });
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); });
app.MapGet("/api/printers", (PollCoordinator poller) => new { printers = store.All().Select(PrinterView.From), activePolls = poller.ActiveCount, serverTime = DateTimeOffset.UtcNow });
app.MapPost("/api/printers", async (PrinterInput input, NetworkPolicy policy, CancellationToken token) =>
{
    input.Validate(); var credentials = input.GetCredentials(null, settings.DemoMode);
    if (!settings.DemoMode) await policy.ResolveAsync(input.Host, input.Port, token);
    if (store.All().Count >= 500) throw new ValidationException("Bu sürüm en fazla 500 cihaz kaydı kabul eder.");
    var row = store.Save(input, credentials);
    return Results.Created($"/api/printers/{row.Config.Id}", PrinterView.From(row));
});
app.MapPut("/api/printers/{id:guid}", async (Guid id, PrinterInput input, NetworkPolicy policy, CancellationToken token) =>
{
    var old = store.Find(id); if (old is null) return Results.NotFound();
    input.Validate(); var credentials = input.GetCredentials(store.Secrets(old), settings.DemoMode);
    if (!settings.DemoMode) await policy.ResolveAsync(input.Host, input.Port, token);
    return Results.Ok(PrinterView.From(store.Save(input, credentials, id)));
});
app.MapDelete("/api/printers/{id:guid}", (Guid id) => { store.Delete(id); return Results.NoContent(); });
app.MapPost("/api/printers/{id:guid}/test", async (Guid id, PollCoordinator poller, CancellationToken token) =>
{
    if (store.Find(id) is null) return Results.NotFound();
    var result = await poller.PollAsync(id, token);
    return Results.Ok(result);
}).RequireRateLimiting("poll");
app.MapPost("/api/refresh", async (PollCoordinator poller, CancellationToken token) =>
{
    await poller.PollAllAsync(token); return Results.Ok(new { message = "Sorgulama tamamlandı." });
}).RequireRateLimiting("poll");
app.MapGet("/api/printers/{id:guid}/history", (Guid id) => Results.Ok(store.History(id)));
app.MapGet("/api/export", () => Results.Json(new { exportedAt = DateTimeOffset.UtcNow, demoMode = settings.DemoMode, printers = store.All().Select(PrinterView.From) }, contentType: "application/json"));
app.MapFallbackToFile("index.html");
app.Run();

public sealed record LoginInput(string? Password);
public partial class Program;
