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

        var uploads = new List<(string FileName, byte[] Content)>();
        var uploadIndexes = new List<int>();
        var results = new KnowledgeFileOperationResult?[form.Files.Count];
        for (var index = 0; index < form.Files.Count; index++)
        {
            var file = form.Files[index];
            if (file.Length > KnowledgeFileParser.MaximumFileSizeBytes)
            {
                results[index] = new KnowledgeFileOperationResult(file.FileName, null, "Invalid", false, "File exceeds the 10 MiB knowledge file limit.");
                continue;
            }

            await using var input = file.OpenReadStream();
            using var output = new MemoryStream();
            await input.CopyToAsync(output);
            uploads.Add((file.FileName, output.ToArray()));
            uploadIndexes.Add(index);
        }

        var importResults = library.ImportFiles(uploads);
        for (var index = 0; index < uploads.Count; index++)
            results[uploadIndexes[index]] = importResults[index];

        return Results.Ok(results);
    }

    private static IResult DeleteSeries(DeleteKnowledgeRequest request, KnowledgeLibrary library)
    {
        if (request.VersionKeys == null || request.VersionKeys.Count == 0)
            return Results.BadRequest(new { error = "Select at least one knowledge entry to delete." });

        var results = library.DeleteSeries(request.VersionKeys);
        return Results.Ok(results);
    }
}