public static class LibraryManagementEndpoints
{
    public static void MapLibraryManagementEndpoints(this WebApplication app)
    {
        app.MapPost("/api/library/upload", UploadFiles).DisableAntiforgery();
        app.MapPost("/api/library/delete", DeleteSeries);
    }

    private static async Task<IResult> UploadFiles(HttpRequest request, KnowledgeLibrary library)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Expected a multipart form upload." });

        var form = await request.ReadFormAsync();
        if (form.Files.Count == 0)
            return Results.BadRequest(new { error = "Choose at least one Markdown file." });

        var results = new List<KnowledgeFileOperationResult>();
        foreach (var file in form.Files)
        {
            await using var input = file.OpenReadStream();
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            results.Add(library.ImportFile(file.FileName, output.ToArray()));
        }

        if (results.Any(result => result.Succeeded))
            library.Rescan();

        return Results.Ok(results);
    }

    private static IResult DeleteSeries(DeleteKnowledgeRequest request, KnowledgeLibrary library)
    {
        if (request.VersionKeys == null || request.VersionKeys.Count == 0)
            return Results.BadRequest(new { error = "Select at least one knowledge entry to delete." });

        var results = library.DeleteSeries(request.VersionKeys);
        if (results.Any(result => result.Succeeded))
            library.Rescan();

        return Results.Ok(results);
    }
}