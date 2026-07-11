public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/library", (KnowledgeLibrary library) => Results.Ok(library.GetStatus()));

        app.MapGet("/api/knowledge", (string? query, KnowledgeLibrary library) =>
            Results.Ok(library.Search(query)));

        app.MapGet("/api/knowledge/{id:guid}/versions", GetVersions);
        app.MapGet("/api/versions/{versionKey}", GetVersion);

        app.MapPost("/api/rescan", (KnowledgeLibrary library) =>
        {
            library.Rescan();
            return Results.Ok(library.GetStatus());
        });
    }

    private static IResult GetVersions(Guid id, KnowledgeLibrary library)
    {
        var versions = library.GetVersions(id);
        return versions == null ? Results.NotFound() : Results.Ok(versions);
    }

    private static IResult GetVersion(string versionKey, KnowledgeLibrary library)
    {
        var version = library.GetVersion(versionKey);
        if (version == null)
            return Results.NotFound();

        return Results.Ok(new VersionDetailResponse(
            version.VersionKey,
            version.SeriesId,
            version.Title,
            version.Created,
            version.Updated,
            version.Summary,
            version.Keywords,
            version.Connections,
            version.Body,
            version.IsCurrent));
    }
}