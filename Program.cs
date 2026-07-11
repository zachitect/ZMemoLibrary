var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<KnowledgeLibrary>();

var app = builder.Build();
var library = app.Services.GetRequiredService<KnowledgeLibrary>();
library.Rescan();

app.UseHttpsRedirection();

var webRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var indexPath = Path.Combine(webRootPath, "index.html");
var scriptPath = Path.Combine(webRootPath, "app.js");

app.MapGet("/", () => ServeFile(indexPath, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => ServeFile(indexPath, "text/html; charset=utf-8"));
app.MapGet("/app.js", () => ServeFile(scriptPath, "text/javascript; charset=utf-8"));

app.MapKnowledgeEndpoints();
app.MapDownloadEndpoints();

app.Run();

IResult ServeFile(string path, string contentType)
{
    return File.Exists(path)
        ? Results.File(path, contentType, enableRangeProcessing: false)
        : Results.Problem(
            $"Required web asset is missing: {path}",
            statusCode: StatusCodes.Status500InternalServerError);
}