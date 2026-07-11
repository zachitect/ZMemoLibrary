public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/versions/{versionKey}/download", DownloadVersion);
    }

    private static IResult DownloadVersion(string versionKey, KnowledgeLibrary library)
    {
        try
        {
            var file = library.ReadVersionFile(versionKey);
            return file == null
                ? Results.NotFound()
                : Results.File(file.Content, "text/markdown; charset=utf-8", file.FileName);
        }
        catch (IOException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}