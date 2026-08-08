using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var settingsStore = new SettingsStore(builder.Environment.ContentRootPath);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(settingsStore.Port));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton<KnowledgeLibrary>();
builder.Services.AddSingleton<AccessStore>();
builder.Services.AddSingleton<MemoStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/access";
        options.AccessDeniedPath = "/access";
        options.Cookie.Name = "ZMemoLibrary.Access";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["Content-Security-Policy"] =
            "frame-ancestors 'self' https://zachitect.github.io";
        context.Response.Headers.Remove("X-Frame-Options");
        return Task.CompletedTask;
    });

    return next();
});
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

app.MapGet("/memo", () => Results.Content(BuildMemoPage(), "text/html; charset=utf-8"));

app.MapGet("/api/memo", (MemoStore memoStore) => Results.Ok(memoStore.Read()));

app.MapPost("/api/memo/lease", (MemoLeaseRequest request, MemoStore memoStore) =>
{
    if (string.IsNullOrWhiteSpace(request.EditorId))
        return Results.BadRequest(new { error = "Editor ID is required." });

    var result = memoStore.TryAcquire(request.EditorId);
    return result.Acquired
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/memo/heartbeat", (MemoLeaseRequest request, MemoStore memoStore) =>
    memoStore.TryRenew(request.EditorId)
        ? Results.NoContent()
        : Results.Json(new { error = "The editing lease is no longer active." }, statusCode: StatusCodes.Status409Conflict));

app.MapPost("/api/memo/release", (MemoLeaseRequest request, MemoStore memoStore) =>
{
    memoStore.Release(request.EditorId);
    return Results.NoContent();
});

app.MapPut("/api/memo", (SaveMemoRequest request, MemoStore memoStore) =>
{
    try
    {
        return Results.Ok(memoStore.Save(request.EditorId, request.Revision, request.Content));
    }
    catch (MemoConflictException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

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
    knowledgeLibrary.ApplySettings(fullKnowledgeLibraryDirectory, port);

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

    knowledgeLibrary.ApplySettings(fullKnowledgeLibraryDirectory, port);
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

string BuildMemoPage()
{
    return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <script>
    (() => {
      const preference = localStorage.getItem("zmemolibrary.themePreference");
      const theme = preference === "light" || preference === "dark"
        ? preference
        : (new Date().getHours() >= 18 || new Date().getHours() < 6 ? "dark" : "light");
      document.documentElement.dataset.theme = theme;
    })();
  </script>
  <title>Memo</title>
  <style>
    :root { color-scheme:dark; font-family:"Cascadia Code","Cascadia Mono",Consolas,"Courier New",monospace; --page:#121821; --surface:#1B2430; --surface-subtle:#151D27; --surface-hover:#202C3A; --text:#F1F5F9; --secondary:#A6B1C0; --border:#39485B; --border-strong:#52647A; --primary:#5F8EE8; --primary-hover:#78A2F0; --error:#E26D67; --success:#63C187; background:var(--page); color:var(--text); }
    :root[data-theme="light"] { color-scheme:light; --page:#F4F7F9; --surface:#FFFFFF; --surface-subtle:#EEF1F4; --surface-hover:#E4E9EF; --text:#263244; --secondary:#586477; --border:#C7CED8; --border-strong:#AAB5C3; --primary:#2563EB; --primary-hover:#1D4ED8; --error:#A4261D; --success:#1F6B3A; }
    * { box-sizing:border-box; }
    html,body { min-width:0; height:100%; margin:0; background:var(--page); color:var(--text); }
    body { overflow:hidden; }
    button,textarea { font:inherit; }
    .shell { width:min(1440px,calc(100% - 32px)); height:100%; min-height:0; margin:0 auto; padding:16px 0 12px; display:grid; grid-template-rows:auto minmax(0,1fr); gap:12px; }
    .top-panel,.memo-section { min-width:0; border:1px solid var(--border); border-radius:7px; background:var(--surface); }
    .top-panel { overflow:hidden; }
    .top-row { width:100%; min-width:0; display:grid; grid-template-columns:minmax(0,1fr) auto; column-gap:12px; align-items:center; padding:0 18px; }
    .identity-row { min-height:72px; border-bottom:1px solid var(--border); }
    .brand-group { min-width:0; display:flex; align-items:center; gap:12px; }
    .brand-copy { min-width:0; }
    h1 { margin:0; font-size:22px; line-height:1.2; letter-spacing:-.35px; overflow-wrap:anywhere; }
    .subtitle { margin:4px 0 0; color:var(--secondary); font-size:11px; line-height:1.4; }
    button,.back { min-width:0; min-height:40px; border:1px solid var(--border-strong); border-radius:5px; padding:7px 12px; display:inline-flex; align-items:center; justify-content:center; background:var(--surface-subtle); color:var(--text); font-size:12px; font-weight:650; line-height:1.25; text-align:center; text-decoration:none; cursor:pointer; }
    button:hover:not(:disabled),.back:hover { border-color:var(--primary); background:var(--surface-hover); }
    button.primary { border-color:var(--primary); background:var(--primary); color:#fff; }
    button.primary:hover:not(:disabled) { border-color:var(--primary-hover); background:var(--primary-hover); }
    button:disabled { opacity:.42; cursor:default; }
    button:focus-visible,.back:focus-visible,textarea:focus-visible,.memo-rendered a:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
    .theme-toggle { width:40px; min-width:40px; max-width:40px; height:40px; padding:0; font-size:18px; line-height:1; }
    .back { width:auto; justify-self:end; white-space:nowrap; }
    .memo-controls { min-height:62px; padding-block:10px; }
    .status { min-width:0; color:var(--secondary); font-size:11px; line-height:1.45; overflow-wrap:anywhere; }
    .status.success { color:var(--success); }
    .status.error { color:var(--error); }
    .button-row { display:flex; align-items:center; justify-content:flex-end; gap:8px; flex-wrap:wrap; }
    .memo-section { min-height:0; overflow:hidden; }
    textarea,.memo-rendered { width:100%; height:100%; min-width:0; min-height:0; border:0; padding:18px; background:var(--surface); color:var(--text); font-size:14px; line-height:1.55; }
    textarea { display:block; resize:none; tab-size:4; }
    textarea:read-only { color:var(--secondary); cursor:default; }
    .memo-rendered { overflow:auto; overflow-wrap:anywhere; }
    .memo-rendered > :first-child { margin-top:0; } .memo-rendered > :last-child { margin-bottom:0; }
    .memo-rendered h1,.memo-rendered h2,.memo-rendered h3,.memo-rendered h4,.memo-rendered h5,.memo-rendered h6 { margin:1.4em 0 .55em; line-height:1.25; }
    .memo-rendered h1 { font-size:1.7em; } .memo-rendered h2 { font-size:1.45em; } .memo-rendered h3 { font-size:1.2em; }
    .memo-rendered p,.memo-rendered ul,.memo-rendered ol,.memo-rendered blockquote,.memo-rendered pre { margin:0 0 1em; }
    .memo-rendered ul,.memo-rendered ol { padding-left:2em; } .memo-rendered li + li { margin-top:.3em; }
    .memo-rendered blockquote { padding-left:14px; border-left:3px solid var(--border-strong); color:var(--secondary); }
    .memo-rendered code { border-radius:4px; padding:.12em .35em; background:var(--page); font:inherit; font-size:.92em; }
    .memo-rendered pre { overflow:auto; padding:14px; border:1px solid var(--border); border-radius:5px; background:var(--page); }
    .memo-rendered pre code { padding:0; background:transparent; } .memo-rendered a { color:var(--primary); } .memo-rendered a:hover { color:var(--primary-hover); }
    .memo-rendered hr { margin:1.4em 0; border:0; border-top:1px solid var(--border); }
    [hidden] { display:none !important; }
    @media (max-width:700px) { .shell { width:calc(100% - 10px); padding:5px 0; gap:5px; } .top-panel,.memo-section { border-radius:6px; } .top-row { column-gap:8px; padding-inline:8px; } .identity-row { min-height:50px; } .brand-group { gap:8px; } h1 { font-size:18px; } .subtitle { display:none; } .back { padding:6px 10px; } .memo-controls { grid-template-columns:minmax(0,1fr); gap:8px; padding-block:8px; } .button-row { display:grid; grid-template-columns:1fr 1fr; width:100%; } .button-row button { width:100%; } textarea,.memo-rendered { padding:14px; font-size:13px; } }
    @media (max-height:620px) and (min-width:701px) { .shell { padding:8px 0; gap:8px; } .identity-row { min-height:58px; } .memo-controls { min-height:52px; padding-block:6px; } }
  </style>
</head>
<body>
  <main class="shell">
    <div class="top-panel">
      <section class="top-row identity-row">
        <div class="brand-group">
          <button id="themeToggle" class="theme-toggle" type="button" aria-label="Switch colour theme" title="Switch colour theme">☀</button>
          <div class="brand-copy"><h1>Memo</h1><p class="subtitle">Read rendered Markdown or acquire the editing lease to update the shared memo.</p></div>
        </div>
        <a class="back" href="/">Back to Library</a>
      </section>
      <section class="top-row memo-controls">
        <div id="status" class="status" role="status" aria-live="polite">Loading memo...</div>
        <div class="button-row">
          <button id="startEditing" class="primary" type="button" disabled>Start Editing</button>
          <button id="save" class="primary" type="button" disabled>Save</button>
          <button id="finishEditing" type="button" disabled>Finish Editing</button>
          <button id="reload" type="button" disabled>Reload</button>
        </div>
      </section>
    </div>
    <section class="memo-section" aria-label="Shared memo">
      <div id="renderedMemo" class="memo-rendered" aria-label="Rendered memo"></div>
      <textarea id="memo" aria-label="Memo Markdown source" maxlength="1048576" spellcheck="true" readonly hidden></textarea>
    </section>
  </main>
  <script>
    (() => {
      const leaseDurationMs = 30000;
      const heartbeatIntervalMs = 10000;
      const inactivityLimitMs = 120000;
      const encoder = new TextEncoder();
      const editorId = sessionStorage.getItem("zmemolibrary.memoEditorId") || crypto.randomUUID();
      sessionStorage.setItem("zmemolibrary.memoEditorId", editorId);

      const textarea = document.querySelector("#memo");
      const renderedMemo = document.querySelector("#renderedMemo");
      const status = document.querySelector("#status");
      const themeToggle = document.querySelector("#themeToggle");
      const startEditing = document.querySelector("#startEditing");
      const save = document.querySelector("#save");
      const finishEditing = document.querySelector("#finishEditing");
      const reload = document.querySelector("#reload");

      let revision = "";
      let savedContent = "";
      let ownsLease = false;
      let showEditor = false;
      let requestInProgress = false;
      let lastActivityAt = 0;
      let heartbeatTimer = null;

      const themePreferenceKey = "zmemolibrary.themePreference";
      let activeTheme = "dark";
      let automaticThemeTimer = null;

      function resolveAutomaticTheme(date = new Date()) {
        const hour = date.getHours();
        return hour >= 18 || hour < 6 ? "dark" : "light";
      }

      function getThemePreference() {
        const preference = localStorage.getItem(themePreferenceKey);
        return preference === "light" || preference === "dark" ? preference : null;
      }

      function applyTheme(theme) {
        activeTheme = theme;
        document.documentElement.dataset.theme = theme;
        document.body.dataset.theme = theme;
        const isDark = theme === "dark";
        themeToggle.textContent = isDark ? "☾" : "☀";
        themeToggle.setAttribute("aria-label", isDark ? "Switch to day theme" : "Switch to night theme");
        themeToggle.title = isDark ? "Switch to day theme" : "Switch to night theme";
      }

      function scheduleAutomaticThemeRefresh() {
        clearTimeout(automaticThemeTimer);
        if (getThemePreference() !== null) return;

        const now = new Date();
        const nextBoundary = new Date(now);
        if (now.getHours() < 6) {
          nextBoundary.setHours(6, 0, 0, 0);
        } else if (now.getHours() < 18) {
          nextBoundary.setHours(18, 0, 0, 0);
        } else {
          nextBoundary.setDate(nextBoundary.getDate() + 1);
          nextBoundary.setHours(6, 0, 0, 0);
        }

        automaticThemeTimer = setTimeout(() => {
          if (getThemePreference() === null) applyTheme(resolveAutomaticTheme());
          scheduleAutomaticThemeRefresh();
        }, nextBoundary.getTime() - now.getTime() + 100);
      }

      function initialiseTheme() {
        applyTheme(getThemePreference() ?? resolveAutomaticTheme());
        scheduleAutomaticThemeRefresh();
      }

      async function request(url, options) {
        const response = await fetch(url, options);
        if (response.status === 401) {
          window.location.assign("/access");
          throw new Error("Access expired.");
        }
        if (!response.ok) {
          let message = `Request failed with status ${response.status}.`;
          try {
            const payload = await response.json();
            if (payload.error) message = payload.error;
          } catch {}
          const error = new Error(message);
          error.status = response.status;
          throw error;
        }
        return response;
      }

      function setStatus(message, kind = "") {
        status.textContent = message;
        status.className = `status ${kind}`.trim();
      }

      function updateControls() {
        const changed = textarea.value !== savedContent;
        textarea.hidden = !showEditor;
        renderedMemo.hidden = showEditor;
        textarea.readOnly = !ownsLease;
        startEditing.disabled = ownsLease || requestInProgress;
        save.disabled = !ownsLease || !changed || requestInProgress;
        finishEditing.disabled = !ownsLease || requestInProgress;
        reload.disabled = ownsLease || requestInProgress;
      }

      function showRenderedMemo() {
        showEditor = false;
        renderedMemo.innerHTML = renderMarkdown(savedContent);
      }

      function formatSavedTime(value) {
        if (!value) return "Memo is empty and has not been saved yet.";
        return `Saved ${new Date(value).toLocaleString()}`;
      }

      async function loadMemo() {
        requestInProgress = true;
        updateControls();
        try {
          const response = await request("/api/memo");
          const memo = await response.json();
          textarea.value = memo.content;
          savedContent = memo.content;
          revision = memo.revision;
          showRenderedMemo();
          setStatus(memo.isLocked ? "Read only. Another session is editing this memo." : formatSavedTime(memo.modifiedUtc), memo.isLocked ? "" : "success");
        } catch (error) {
          setStatus(error.message, "error");
        } finally {
          requestInProgress = false;
          updateControls();
        }
      }

      async function acquireLease() {
        requestInProgress = true;
        updateControls();
        setStatus("Requesting editing access...");
        try {
          const response = await request("/api/memo/lease", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ editorId })
          });
          const result = await response.json();
          textarea.value = result.memo.content;
          savedContent = result.memo.content;
          revision = result.memo.revision;
          ownsLease = true;
          showEditor = true;
          lastActivityAt = Date.now();
          startHeartbeat();
          setStatus("Editing. Save explicitly when ready.", "success");
          textarea.focus();
        } catch (error) {
          ownsLease = false;
          setStatus(error.status === 409 ? "Read only. Another session acquired the editing lease first." : error.message, error.status === 409 ? "" : "error");
        } finally {
          requestInProgress = false;
          updateControls();
        }
      }

      function startHeartbeat() {
        clearInterval(heartbeatTimer);
        heartbeatTimer = setInterval(sendHeartbeat, heartbeatIntervalMs);
      }

      async function sendHeartbeat() {
        if (!ownsLease) return;
        if (Date.now() - lastActivityAt >= inactivityLimitMs) {
          clearInterval(heartbeatTimer);
          heartbeatTimer = null;
          ownsLease = false;
          if (textarea.value === savedContent) showRenderedMemo();
          setStatus("Editing paused after 2 minutes of inactivity. Unsaved changes were not saved.", "error");
          updateControls();
          return;
        }

        try {
          await request("/api/memo/heartbeat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ editorId })
          });
        } catch (error) {
          clearInterval(heartbeatTimer);
          heartbeatTimer = null;
          ownsLease = false;
          if (textarea.value === savedContent) showRenderedMemo();
          setStatus(error.status === 409 ? "Editing lease expired. Reload before editing again." : error.message, "error");
          updateControls();
        }
      }

      async function saveMemo() {
        if (!ownsLease || requestInProgress) return;
        if (encoder.encode(textarea.value).length > 1024 * 1024) {
          setStatus("Memo exceeds the 1 MiB limit.", "error");
          return;
        }

        requestInProgress = true;
        lastActivityAt = Date.now();
        updateControls();
        setStatus("Saving...");
        try {
          const response = await request("/api/memo", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ editorId, revision, content: textarea.value })
          });
          const memo = await response.json();
          savedContent = memo.content;
          revision = memo.revision;
          setStatus(formatSavedTime(memo.modifiedUtc), "success");
        } catch (error) {
          if (error.status === 409) {
            ownsLease = false;
            clearInterval(heartbeatTimer);
            heartbeatTimer = null;
            if (textarea.value === savedContent) showRenderedMemo();
          }
          setStatus(error.message, "error");
        } finally {
          requestInProgress = false;
          updateControls();
        }
      }

      async function releaseLease() {
        if (!ownsLease) return true;
        if (textarea.value !== savedContent && !window.confirm("Discard unsaved changes and finish editing?")) return false;

        requestInProgress = true;
        updateControls();
        try {
          await request("/api/memo/release", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ editorId })
          });
          ownsLease = false;
          clearInterval(heartbeatTimer);
          heartbeatTimer = null;
          await loadMemo();
          return true;
        } catch (error) {
          setStatus(error.message, "error");
          return false;
        } finally {
          requestInProgress = false;
          updateControls();
        }
      }

  function renderMarkdown(markdown) {
    const escaped = escapeHtml(markdown).replace(/\r\n?/g, "\n");
    const codeBlocks = [];
    const protectedText = escaped.replace(/```([^\n]*)\n([\s\S]*?)```/g, (_, language, code) => {
      const index = codeBlocks.length;
      const languageClass = language.trim() ? ` class="language-${escapeAttribute(language.trim())}"` : "";
      codeBlocks.push(`<pre><code${languageClass}>${code.replace(/\n$/, "")}</code></pre>`);
      return `\n@@CODEBLOCK${index}@@\n`;
    });

    const lines = protectedText.split("\n");
    const html = [];
    let paragraph = [];
    let listType = null;
    let blockquote = [];

    function flushParagraph() {
      if (paragraph.length === 0) return;
      html.push(`<p>${formatInline(paragraph.join(" "))}</p>`);
      paragraph = [];
    }

    function closeList() {
      if (!listType) return;
      html.push(`</${listType}>`);
      listType = null;
    }

    function flushBlockquote() {
      if (blockquote.length === 0) return;
      html.push(`<blockquote>${formatInline(blockquote.join(" "))}</blockquote>`);
      blockquote = [];
    }

    for (const line of lines) {
      const trimmed = line.trim();
      const codeMatch = trimmed.match(/^@@CODEBLOCK(\d+)@@$/);
      if (codeMatch) {
        flushParagraph();
        closeList();
        flushBlockquote();
        html.push(codeBlocks[Number(codeMatch[1])]);
        continue;
      }

      if (trimmed === "") {
        flushParagraph();
        closeList();
        flushBlockquote();
        continue;
      }

      const heading = line.match(/^(#{1,6})\s+(.+)$/);
      if (heading) {
        flushParagraph();
        closeList();
        flushBlockquote();
        const level = heading[1].length;
        html.push(`<h${level}>${formatInline(heading[2])}</h${level}>`);
        continue;
      }

      if (/^([-*_])(?:\s*\1){2,}\s*$/.test(trimmed)) {
        flushParagraph();
        closeList();
        flushBlockquote();
        html.push("<hr>");
        continue;
      }

      const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
      const ordered = line.match(/^\s*\d+[.)]\s+(.+)$/);
      if (unordered || ordered) {
        flushParagraph();
        flushBlockquote();
        const requestedType = unordered ? "ul" : "ol";
        if (listType !== requestedType) {
          closeList();
          listType = requestedType;
          html.push(`<${listType}>`);
        }
        html.push(`<li>${formatInline((unordered || ordered)[1])}</li>`);
        continue;
      }

      const quote = line.match(/^\s*>\s?(.*)$/);
      if (quote) {
        flushParagraph();
        closeList();
        blockquote.push(quote[1]);
        continue;
      }

      closeList();
      flushBlockquote();
      paragraph.push(trimmed);
    }

    flushParagraph();
    closeList();
    flushBlockquote();
    return html.join("\n");
  }

  function formatInline(text) {
    return text
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/__([^_]+)__/g, "<strong>$1</strong>")
      .replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, "<em>$1</em>")
      .replace(/(?<!_)_([^_]+)_(?!_)/g, "<em>$1</em>")
      .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
  }

  function escapeHtml(value) {
    return value
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#039;");
  }

  function escapeAttribute(value) {
    return value.replace(/[^a-zA-Z0-9_-]/g, "");
  }

      textarea.addEventListener("input", () => {
        lastActivityAt = Date.now();
        setStatus(textarea.value === savedContent ? "No unsaved changes." : "Unsaved changes.");
        updateControls();
      });
      textarea.addEventListener("focus", () => { if (ownsLease) lastActivityAt = Date.now(); });
      textarea.addEventListener("click", () => { if (ownsLease) lastActivityAt = Date.now(); });
      themeToggle.addEventListener("click", () => {
        const theme = activeTheme === "dark" ? "light" : "dark";
        localStorage.setItem(themePreferenceKey, theme);
        applyTheme(theme);
        scheduleAutomaticThemeRefresh();
      });

      window.addEventListener("storage", event => {
        if (event.key !== themePreferenceKey) return;
        applyTheme(event.newValue === "light" || event.newValue === "dark" ? event.newValue : resolveAutomaticTheme());
        scheduleAutomaticThemeRefresh();
      });
      startEditing.addEventListener("click", acquireLease);
      save.addEventListener("click", saveMemo);
      finishEditing.addEventListener("click", releaseLease);
      reload.addEventListener("click", () => {
        if (showEditor && textarea.value !== savedContent && !window.confirm("Discard unsaved changes and reload the saved memo?")) return;
        void loadMemo();
      });
      document.querySelector(".back").addEventListener("click", async event => {
        if (!ownsLease) return;
        event.preventDefault();
        if (await releaseLease()) window.location.assign("/");
      });
      window.addEventListener("beforeunload", event => {
        if (ownsLease && textarea.value !== savedContent) {
          event.preventDefault();
          event.returnValue = "";
        }
      });
      window.addEventListener("pagehide", () => {
        if (!ownsLease) return;
        navigator.sendBeacon("/api/memo/release", new Blob([JSON.stringify({ editorId })], { type: "application/json" }));
      });

      initialiseTheme();
      void loadMemo();
    })();
  </script>
</body>
</html>
""";
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
  <script>
    (() => {
      const preference = localStorage.getItem("zmemolibrary.themePreference");
      const theme = preference === "light" || preference === "dark"
        ? preference
        : (new Date().getHours() >= 18 || new Date().getHours() < 6 ? "dark" : "light");
      document.documentElement.dataset.theme = theme;
    })();
  </script>
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
    :root[data-theme="light"] {
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
    :root[data-theme="light"] main { box-shadow: 0 10px 30px rgba(38,50,68,.12); }
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
  <script>
    (() => {
      const themePreferenceKey = "zmemolibrary.themePreference";
      let automaticThemeTimer = null;

      function resolveAutomaticTheme(date = new Date()) {
        const hour = date.getHours();
        return hour >= 18 || hour < 6 ? "dark" : "light";
      }

      function getThemePreference() {
        const preference = localStorage.getItem(themePreferenceKey);
        return preference === "light" || preference === "dark" ? preference : null;
      }

      function applyTheme(theme) {
        document.documentElement.dataset.theme = theme;
        document.body.dataset.theme = theme;
      }

      function scheduleAutomaticThemeRefresh() {
        clearTimeout(automaticThemeTimer);
        if (getThemePreference() !== null) return;

        const now = new Date();
        const nextBoundary = new Date(now);
        if (now.getHours() < 6) {
          nextBoundary.setHours(6, 0, 0, 0);
        } else if (now.getHours() < 18) {
          nextBoundary.setHours(18, 0, 0, 0);
        } else {
          nextBoundary.setDate(nextBoundary.getDate() + 1);
          nextBoundary.setHours(6, 0, 0, 0);
        }

        automaticThemeTimer = setTimeout(() => {
          if (getThemePreference() === null) applyTheme(resolveAutomaticTheme());
          scheduleAutomaticThemeRefresh();
        }, nextBoundary.getTime() - now.getTime() + 100);
      }

      applyTheme(getThemePreference() ?? resolveAutomaticTheme());
      scheduleAutomaticThemeRefresh();

      window.addEventListener("storage", event => {
        if (event.key !== themePreferenceKey) return;
        applyTheme(event.newValue === "light" || event.newValue === "dark" ? event.newValue : resolveAutomaticTheme());
        scheduleAutomaticThemeRefresh();
      });
    })();
  </script>
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
            var record = JsonSerializer.Deserialize<AccessRecord>(File.ReadAllText(path, Encoding.UTF8))
                ?? throw new InvalidDataException("The access configuration is empty.");
            if (string.IsNullOrEmpty(record.Salt) || string.IsNullOrEmpty(record.Hash))
                throw new InvalidDataException("The access configuration contains invalid passcode parameters.");
            var salt = Convert.FromBase64String(record.Salt);
            var hash = Convert.FromBase64String(record.Hash);
            if (salt.Length != 16 || hash.Length != 32 || record.Iterations != Iterations)
                throw new InvalidDataException("The access configuration contains invalid passcode parameters.");
            if (string.IsNullOrEmpty(record.SessionVersion) || record.SessionVersion.Length != 64 || Convert.FromHexString(record.SessionVersion).Length != 32)
                throw new InvalidDataException("The access configuration contains an invalid session version.");
            return record;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            throw new InvalidOperationException($"Could not read access configuration: {path}", ex);
        }
    }

    private sealed record AccessRecord(string Salt, string Hash, int Iterations, string SessionVersion);
}

public sealed class MemoStore
{
    public const int MaximumContentSizeBytes = 1024 * 1024;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly object _lock = new();
    private readonly string _path;
    private string? _editorId;
    private DateTimeOffset _leaseExpiresUtc;

    public MemoStore(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "AppData", "memo.md");
    }

    public MemoResponse Read()
    {
        lock (_lock)
        {
            ClearExpiredLease();
            return ReadCore(_editorId != null);
        }
    }

    public MemoLeaseResponse TryAcquire(string editorId)
    {
        lock (_lock)
        {
            ClearExpiredLease();
            if (_editorId != null && !string.Equals(_editorId, editorId, StringComparison.Ordinal))
                return new MemoLeaseResponse(false, ReadCore(true));

            _editorId = editorId;
            _leaseExpiresUtc = DateTimeOffset.UtcNow.Add(LeaseDuration);
            return new MemoLeaseResponse(true, ReadCore(true));
        }
    }

    public bool TryRenew(string editorId)
    {
        lock (_lock)
        {
            ClearExpiredLease();
            if (!string.Equals(_editorId, editorId, StringComparison.Ordinal))
                return false;

            _leaseExpiresUtc = DateTimeOffset.UtcNow.Add(LeaseDuration);
            return true;
        }
    }

    public void Release(string editorId)
    {
        lock (_lock)
        {
            ClearExpiredLease();
            if (!string.Equals(_editorId, editorId, StringComparison.Ordinal))
                return;

            _editorId = null;
            _leaseExpiresUtc = default;
        }
    }

    public MemoResponse Save(string editorId, string revision, string content)
    {
        if (string.IsNullOrWhiteSpace(editorId))
            throw new InvalidDataException("Editor ID is required.");
        if (revision == null)
            throw new InvalidDataException("Memo revision is required.");
        if (content == null)
            throw new InvalidDataException("Memo content is required.");

        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaximumContentSizeBytes)
            throw new InvalidDataException("Memo exceeds the 1 MiB limit.");

        lock (_lock)
        {
            ClearExpiredLease();
            if (!string.Equals(_editorId, editorId, StringComparison.Ordinal))
                throw new MemoConflictException("The editing lease is no longer active.");

            var current = ReadCore(true);
            if (!string.Equals(current.Revision, revision, StringComparison.Ordinal))
                throw new MemoConflictException("The memo changed after this editor loaded it. Reload before editing again.");

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".memo-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            _leaseExpiresUtc = DateTimeOffset.UtcNow.Add(LeaseDuration);
            return ReadCore(true);
        }
    }

    private MemoResponse ReadCore(bool isLocked)
    {
        var bytes = File.Exists(_path) ? File.ReadAllBytes(_path) : [];
        var content = new UTF8Encoding(false, true).GetString(bytes);
        var revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var modifiedUtc = File.Exists(_path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero) : (DateTimeOffset?)null;
        return new MemoResponse(content, revision, modifiedUtc, isLocked);
    }

    private void ClearExpiredLease()
    {
        if (_editorId == null || _leaseExpiresUtc > DateTimeOffset.UtcNow)
            return;

        _editorId = null;
        _leaseExpiresUtc = default;
    }
}

public sealed class MemoConflictException : Exception
{
    public MemoConflictException(string message) : base(message)
    {
    }
}

public sealed record MemoLeaseRequest(string EditorId);
public sealed record SaveMemoRequest(string EditorId, string Revision, string Content);
public sealed record MemoResponse(string Content, string Revision, DateTimeOffset? ModifiedUtc, bool IsLocked);
public sealed record MemoLeaseResponse(bool Acquired, MemoResponse Memo);