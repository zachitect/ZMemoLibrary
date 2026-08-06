using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var settingsStore = new SettingsStore(builder.Environment.ContentRootPath);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(settingsStore.Port));

builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton<KnowledgeLibrary>();
builder.Services.AddSingleton<AccessStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/access";
        options.AccessDeniedPath = "/access";
        options.Cookie.Name = "ZMemoLibrary.Access";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = context =>
        {
            var accessStore = context.HttpContext.RequestServices.GetRequiredService<AccessStore>();
            var sessionVersion = context.Principal?.FindFirstValue("session_version");
            if (!accessStore.IsConfigured || !accessStore.IsCurrentSession(sessionVersion))
                context.RejectPrincipal();

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("access", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();
var library = app.Services.GetRequiredService<KnowledgeLibrary>();
if (settingsStore.IsConfigured)
    library.Rescan();

app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
    var isSetupRequest = context.Request.Path.StartsWithSegments("/setup");
    var isAccessRequest = context.Request.Path.StartsWithSegments("/access");
    var isLogoutRequest = context.Request.Path.StartsWithSegments("/logout");

    if (isAuthenticated && !settingsStore.IsConfigured && !isSetupRequest && !isAccessRequest && !isLogoutRequest)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Application setup is required." });
        }
        else
        {
            context.Response.Redirect("/setup");
        }

        return;
    }

    await next();
});
app.UseAuthorization();

var webRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var indexPath = Path.Combine(webRootPath, "index.html");
var scriptPath = Path.Combine(webRootPath, "app.js");

app.MapGet("/access", (AccessStore accessStore, string? error) =>
    Results.Content(BuildAccessPage(accessStore.IsConfigured, error), "text/html; charset=utf-8"))
    .AllowAnonymous();

app.MapPost("/access", async (HttpContext context, AccessStore accessStore) =>
{
    var form = await context.Request.ReadFormAsync();
    var passcode = form["passcode"].ToString();

    if (!accessStore.IsConfigured)
    {
        var confirmation = form["confirmation"].ToString();
        if (passcode.Length < 12)
            return Results.Redirect("/access?error=Passcode+must+be+at+least+12+characters.");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(passcode), Encoding.UTF8.GetBytes(confirmation)))
            return Results.Redirect("/access?error=Passcodes+did+not+match.");
        if (!accessStore.TryCreate(passcode))
            return Results.Redirect("/access?error=Access+was+already+configured.+Enter+the+existing+passcode.");
    }
    else if (!accessStore.Verify(passcode))
    {
        return Results.Redirect("/access?error=Invalid+passcode.");
    }

    await SignIn(context, accessStore.SessionVersion);
    return Results.Redirect(settingsStore.IsConfigured ? "/" : "/setup");
}).AllowAnonymous().RequireRateLimiting("access");

app.MapGet("/setup", (SettingsStore settings, string? error) =>
{
    if (settings.IsConfigured)
        return Results.Redirect("/settings");

    return Results.Content(BuildSetupPage(settings.Port, error), "text/html; charset=utf-8");
});

app.MapPost("/setup", (HttpRequest request, SettingsStore settings, KnowledgeLibrary knowledgeLibrary) =>
{
    if (settings.IsConfigured)
        return Task.FromResult<IResult>(Results.Redirect("/settings"));

    return SaveInitialSettings(request, settings, knowledgeLibrary);
}).RequireRateLimiting("access");

app.MapGet("/settings", (SettingsStore settings, string? message, string? error) =>
    Results.Content(BuildSettingsPage(settings.KnowledgeLibraryDirectory, settings.Port, message, error), "text/html; charset=utf-8"));

app.MapPost("/settings", async (HttpContext context, AccessStore accessStore, SettingsStore settings, KnowledgeLibrary knowledgeLibrary) =>
{
    var form = await context.Request.ReadFormAsync();
    var currentPasscode = form["currentPasscode"].ToString();
    var knowledgeLibraryDirectory = form["knowledgeLibraryDirectory"].ToString().Trim();
    var portText = form["port"].ToString().Trim();
    var newPasscode = form["newPasscode"].ToString();
    var confirmation = form["confirmation"].ToString();

    if (!accessStore.Verify(currentPasscode))
        return Results.Redirect("/settings?error=Current+passcode+is+incorrect.");
    if (string.IsNullOrWhiteSpace(knowledgeLibraryDirectory))
        return Results.Redirect("/settings?error=Knowledge+library+directory+is+required.");
    if (!Path.IsPathFullyQualified(knowledgeLibraryDirectory))
        return Results.Redirect("/settings?error=Knowledge+library+directory+must+be+an+absolute+server+path.");

    string fullKnowledgeLibraryDirectory;
    try
    {
        fullKnowledgeLibraryDirectory = Path.GetFullPath(knowledgeLibraryDirectory);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return Results.Redirect("/settings?error=Knowledge+library+directory+is+invalid.");
    }

    if (!Directory.Exists(fullKnowledgeLibraryDirectory))
        return Results.Redirect("/settings?error=Knowledge+library+directory+does+not+exist.");
    if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
        return Results.Redirect("/settings?error=Port+must+be+between+1024+and+65535.");
    if (newPasscode.Length > 0 && newPasscode.Length < 12)
        return Results.Redirect("/settings?error=New+passcode+must+be+at+least+12+characters.");
    if (newPasscode.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(newPasscode), Encoding.UTF8.GetBytes(confirmation)))
        return Results.Redirect("/settings?error=New+passcodes+did+not+match.");

    var portChanged = settings.Port != port;
    settings.Save(fullKnowledgeLibraryDirectory, port);
    knowledgeLibrary.Rescan();

    if (newPasscode.Length > 0)
    {
        accessStore.Change(newPasscode);
        await context.SignOutAsync();
        return Results.Redirect("/access");
    }

    var message = portChanged
        ? "Settings+saved.+Restart+ZMemoLibrary+to+apply+the+new+port."
        : "Settings+saved.";
    return Results.Redirect($"/settings?message={message}");
}).RequireRateLimiting("access");

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Redirect("/access");
});

app.MapGet("/", (HttpContext context) => ServeFile(context, indexPath, "text/html; charset=utf-8"));
app.MapGet("/index.html", (HttpContext context) => ServeFile(context, indexPath, "text/html; charset=utf-8"));
app.MapGet("/app.js", (HttpContext context) => ServeFile(context, scriptPath, "text/javascript; charset=utf-8"));

app.MapKnowledgeEndpoints();
app.MapDownloadEndpoints();
app.MapPromptEndpoints();
app.MapLibraryManagementEndpoints();

app.Run();

async Task<IResult> SaveInitialSettings(HttpRequest request, SettingsStore settings, KnowledgeLibrary knowledgeLibrary)
{
    var form = await request.ReadFormAsync();
    var knowledgeLibraryDirectory = form["knowledgeLibraryDirectory"].ToString().Trim();
    var portText = form["port"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(knowledgeLibraryDirectory))
        return Results.Redirect("/setup?error=Knowledge+library+directory+is+required.");
    if (!Path.IsPathFullyQualified(knowledgeLibraryDirectory))
        return Results.Redirect("/setup?error=Knowledge+library+directory+must+be+an+absolute+server+path.");

    string fullKnowledgeLibraryDirectory;
    try
    {
        fullKnowledgeLibraryDirectory = Path.GetFullPath(knowledgeLibraryDirectory);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return Results.Redirect("/setup?error=Knowledge+library+directory+is+invalid.");
    }

    if (!Directory.Exists(fullKnowledgeLibraryDirectory))
        return Results.Redirect("/setup?error=Knowledge+library+directory+does+not+exist+on+the+server.");
    if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
        return Results.Redirect("/setup?error=Port+must+be+between+1024+and+65535.");

    settings.Save(fullKnowledgeLibraryDirectory, port);
    knowledgeLibrary.Rescan();
    return Results.Redirect("/");
}

async Task SignIn(HttpContext context, string sessionVersion)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.Name, "Shared access"),
        new Claim("session_version", sessionVersion)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity));
}

IResult ServeFile(HttpContext context, string path, string contentType)
{
    if (!File.Exists(path))
    {
        return Results.Problem(
            $"Required web asset is missing: {path}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    return Results.File(path, contentType, enableRangeProcessing: false);
}

string BuildAccessPage(bool isConfigured, string? error)
{
    var title = isConfigured ? "Unlock ZMemoLibrary" : "Set up ZMemoLibrary access";
    var description = isConfigured
        ? "Enter the shared passcode to continue."
        : "Create the shared passcode. You will need it on every device.";
    var confirmation = isConfigured
        ? string.Empty
        : "<label for=\"confirmation\">Confirm passcode</label><input id=\"confirmation\" name=\"confirmation\" type=\"password\" minlength=\"12\" autocomplete=\"new-password\" required>";
    var button = isConfigured ? "Unlock" : "Set passcode";
    return BuildAccessDocument(title, description, error, $"<form method=\"post\" action=\"/access\"><label for=\"passcode\">Passcode</label><input id=\"passcode\" name=\"passcode\" type=\"password\" minlength=\"{(isConfigured ? 1 : 12)}\" autocomplete=\"{(isConfigured ? "current-password" : "new-password")}\" autofocus required>{confirmation}<button type=\"submit\">{button}</button></form>");
}

string BuildSetupPage(int port, string? error)
{
    var encodedPort = System.Net.WebUtility.HtmlEncode(port.ToString());
    var form = $"""
<form method="post" action="/setup">
  <label for="knowledgeLibraryDirectory">Knowledge library directory</label>
  <input id="knowledgeLibraryDirectory" name="knowledgeLibraryDirectory" autocomplete="off" autofocus required>
  <div class="hint">Enter an absolute path on the server, for example C:\KnowledgeFiles on Windows or /srv/zmemolibrary/knowledge on Linux.</div>
  <label for="port">HTTP port</label>
  <input id="port" name="port" type="number" min="1024" max="65535" value="{encodedPort}" required>
  <div class="hint">Port changes take effect after the next application restart.</div>
  <button type="submit">Save and open library</button>
</form>
""";
    return BuildAccessDocument("Set up ZMemoLibrary", "Choose the server directory containing the Markdown knowledge files.", error, form);
}

string BuildSettingsPage(string knowledgeLibraryDirectory, int port, string? message, string? error)
{
    var encodedDirectory = System.Net.WebUtility.HtmlEncode(knowledgeLibraryDirectory);
    var encodedPort = System.Net.WebUtility.HtmlEncode(port.ToString());
    var messageMarkup = string.IsNullOrWhiteSpace(message)
        ? string.Empty
        : $"<p class=\"success\">{System.Net.WebUtility.HtmlEncode(message)}</p>";
    var form = $"""
{messageMarkup}
<form method="post" action="/settings">
  <fieldset>
    <legend>Application</legend>
    <label for="knowledgeLibraryDirectory">Knowledge library directory</label>
    <input id="knowledgeLibraryDirectory" name="knowledgeLibraryDirectory" value="{encodedDirectory}" autocomplete="off" required>
    <label for="port">Port number</label>
    <input id="port" name="port" type="number" min="1024" max="65535" value="{encodedPort}" required>
    <div class="hint">Changing the port requires an application restart.</div>
  </fieldset>
  <fieldset>
    <legend>Shared passcode</legend>
    <label for="newPasscode">New passcode</label>
    <input id="newPasscode" name="newPasscode" type="password" minlength="12" autocomplete="new-password">
    <label for="confirmation">Confirm new passcode</label>
    <input id="confirmation" name="confirmation" type="password" minlength="12" autocomplete="new-password">
    <div class="hint">Leave both fields empty to keep the existing passcode.</div>
  </fieldset>
  <fieldset>
    <legend>Confirm changes</legend>
    <label for="currentPasscode">Current passcode</label>
    <input id="currentPasscode" name="currentPasscode" type="password" autocomplete="current-password" required>
  </fieldset>
  <button type="submit">Save settings</button>
</form>
<a class="back" href="/">Back to library</a>
""";
    return BuildAccessDocument("Settings", "Manage the shared passcode and application location settings.", error, form);
}

string BuildAccessDocument(string title, string description, string? error, string content)
{
    var errorMarkup = string.IsNullOrWhiteSpace(error)
        ? string.Empty
        : $"<p class=\"error\">{System.Net.WebUtility.HtmlEncode(error)}</p>";
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{title}}</title>
  <style>
    :root {
      color-scheme: dark;
      font-family: "Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace;
      --page: #121821;
      --surface: #1B2430;
      --surface-subtle: #151D27;
      --text: #F1F5F9;
      --secondary: #A6B1C0;
      --border: #39485B;
      --border-strong: #52647A;
      --primary: #5F8EE8;
      --primary-hover: #78A2F0;
      --error: #E26D67;
      --error-fill: #382127;
      --success: #63C187;
      --success-fill: #193225;
      background: var(--page);
      color: var(--text);
    }
    * { box-sizing: border-box; }
    html { min-width: 0; min-height: 100%; background: var(--page); }
    body {
      min-width: 0; min-height: 100vh; min-height: 100dvh;
      margin: 0;
      display: grid;
      place-items: center;
      padding: max(20px, env(safe-area-inset-top)) max(20px, env(safe-area-inset-right)) max(20px, env(safe-area-inset-bottom)) max(20px, env(safe-area-inset-left));
      overflow-x: hidden;
      background: var(--page);
      color: var(--text);
    }
    main { width: min(480px, 100%); min-width: 0; padding: 30px; border: 1px solid var(--border); border-radius: 7px; background: var(--surface); box-shadow: 0 18px 48px rgba(0,0,0,.3); }
    h1 { margin: 0 0 8px; color: var(--text); font-size: 23px; line-height: 1.25; letter-spacing: -.3px; overflow-wrap: anywhere; }
    p { margin: 0 0 22px; color: var(--secondary); font-size: 13px; line-height: 1.5; overflow-wrap: anywhere; }
    .error, .success { padding: 10px 12px; border-radius: 5px; }
    .error { border: 1px solid var(--error); color: var(--error); background: var(--error-fill); }
    .success { border: 1px solid var(--success); color: var(--success); background: var(--success-fill); }
    form { min-width: 0; display: grid; gap: 10px; }
    fieldset { min-width: 0; display: grid; gap: 10px; margin: 5px 0; padding: 14px; border: 1px solid var(--border); border-radius: 5px; background: var(--surface-subtle); }
    legend { max-width: 100%; padding: 0 6px; color: var(--text); font-weight: 700; overflow-wrap: anywhere; }
    label { min-width: 0; margin-top: 4px; color: var(--text); font-size: 12px; font-weight: 650; overflow-wrap: anywhere; }
    .hint { min-width: 0; color: var(--secondary); font-size: 11px; line-height: 1.4; overflow-wrap: anywhere; }
    input { width: 100%; min-width: 0; height: 44px; border: 1px solid var(--border-strong); border-radius: 5px; padding: 0 12px; background: var(--surface-subtle); color: var(--text); font: inherit; font-size: 16px; }
    input:focus { outline: 2px solid var(--primary); outline-offset: 1px; border-color: var(--primary); }
    button { min-width: 0; min-height: 44px; margin-top: 10px; border: 1px solid var(--primary); border-radius: 5px; padding: 8px 14px; background: var(--primary); color: #fff; font: inherit; font-weight: 700; line-height: 1.25; white-space: normal; overflow-wrap: anywhere; cursor: pointer; }
    button:hover { border-color: var(--primary-hover); background: var(--primary-hover); }
    button:focus-visible, .back:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
    .back { display: inline-block; margin-top: 18px; color: var(--primary); overflow-wrap: anywhere; }
    @media (prefers-color-scheme: light) {
      :root {
        color-scheme: light;
        --page: #F4F7F9;
        --surface: #FFFFFF;
        --surface-subtle: #EEF1F4;
        --text: #263244;
        --secondary: #586477;
        --border: #C7CED8;
        --border-strong: #AAB5C3;
        --primary: #2563EB;
        --primary-hover: #1D4ED8;
        --error: #A4261D;
        --error-fill: #FDE7E5;
        --success: #1F6B3A;
        --success-fill: #E3F3E8;
      }
      main { box-shadow: 0 10px 30px rgba(38,50,68,.12); }
    }
    @media (max-width: 480px) {
      body { place-items: start center; padding: max(10px, env(safe-area-inset-top)) max(10px, env(safe-area-inset-right)) max(10px, env(safe-area-inset-bottom)) max(10px, env(safe-area-inset-left)); }
      main { padding: 22px 16px; border-radius: 6px; }
      h1 { font-size: 21px; }
      fieldset { padding: 12px 10px; }
    }
    @media (max-height: 650px) { body { place-items: start center; } }
    @media (prefers-reduced-motion: reduce) { *, *::before, *::after { transition-duration: .01ms !important; animation-duration: .01ms !important; } }
  </style>
</head>
<body>
  <main>
    <h1>{{title}}</h1>
    <p>{{description}}</p>
    {{errorMarkup}}
    {{content}}
  </main>
</body>
</html>
""";
}

public sealed class SettingsStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private SettingsRecord? _record;

    public SettingsStore(string contentRootPath)
    {
        _path = Path.Combine(contentRootPath, "AppData", "settings.json");
        _record = TryReadRecord(_path);
    }

    public bool IsConfigured
    {
        get { lock (_lock) return _record != null; }
    }

    public string KnowledgeLibraryDirectory
    {
        get { lock (_lock) return _record?.KnowledgeLibraryDirectory ?? throw new InvalidOperationException("Application setup is required."); }
    }

    public int Port
    {
        get { lock (_lock) return _record?.Port ?? 9000; }
    }

    public void Save(string knowledgeLibraryDirectory, int port)
    {
        lock (_lock)
        {
            _record = new SettingsRecord(knowledgeLibraryDirectory, port);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_record), Encoding.UTF8);
            File.Move(temporaryPath, _path, true);
        }
    }

    private static SettingsRecord? TryReadRecord(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var record = JsonSerializer.Deserialize<SettingsRecord>(File.ReadAllText(path, Encoding.UTF8));
            if (record == null || string.IsNullOrWhiteSpace(record.KnowledgeLibraryDirectory))
                return null;
            if (!Path.IsPathFullyQualified(record.KnowledgeLibraryDirectory))
                return null;
            if (record.Port is < 1024 or > 65535)
                return null;

            return record with { KnowledgeLibraryDirectory = Path.GetFullPath(record.KnowledgeLibraryDirectory) };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record SettingsRecord(string KnowledgeLibraryDirectory, int Port);
}

public sealed class AccessStore
{
    private const int Iterations = 210_000;
    private readonly object _lock = new();
    private readonly string _path;
    private AccessRecord? _record;

    public AccessStore(IHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "AppData", "access.json");
        if (File.Exists(_path))
            _record = ReadRecord(_path);
    }

    public bool IsConfigured
    {
        get { lock (_lock) return _record != null; }
    }

    public string SessionVersion
    {
        get { lock (_lock) return _record?.SessionVersion ?? throw new InvalidOperationException("Access is not configured."); }
    }

    public bool TryCreate(string passcode)
    {
        lock (_lock)
        {
            if (_record != null || File.Exists(_path))
                return false;

            _record = CreateRecord(passcode);
            WriteRecord(_record);
            return true;
        }
    }

    public bool Verify(string passcode)
    {
        lock (_lock)
        {
            if (_record == null)
                return false;

            var salt = Convert.FromBase64String(_record.Salt);
            var expectedHash = Convert.FromBase64String(_record.Hash);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(passcode, salt, _record.Iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
    }

    public bool IsCurrentSession(string? sessionVersion)
    {
        lock (_lock)
            return _record != null && string.Equals(_record.SessionVersion, sessionVersion, StringComparison.Ordinal);
    }

    public void Change(string passcode)
    {
        lock (_lock)
        {
            if (_record == null)
                throw new InvalidOperationException("Access is not configured.");

            _record = CreateRecord(passcode);
            WriteRecord(_record);
        }
    }

    private static AccessRecord CreateRecord(string passcode)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(passcode, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new AccessRecord(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    private void WriteRecord(AccessRecord record)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record), Encoding.UTF8);
        File.Move(temporaryPath, _path, true);
    }

    private static AccessRecord ReadRecord(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AccessRecord>(File.ReadAllText(path, Encoding.UTF8))
                ?? throw new InvalidDataException("The access configuration is empty.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read access configuration: {path}", ex);
        }
    }

    private sealed record AccessRecord(string Salt, string Hash, int Iterations, string SessionVersion);
}