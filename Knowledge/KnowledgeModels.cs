public sealed class KnowledgeVersion
{
    public KnowledgeVersion(
        Guid seriesId,
        string versionKey,
        string fullPath,
        string relativePath,
        string fileName,
        string title,
        DateOnly created,
        DateOnly updated,
        DateTime fileModifiedUtc,
        DateTime fileCreatedUtc,
        string summary,
        List<string> keywords,
        List<KnowledgeConnection> connections,
        string body)
    {
        SeriesId = seriesId;
        VersionKey = versionKey;
        FullPath = fullPath;
        RelativePath = relativePath;
        FileName = fileName;
        Title = title;
        Created = created;
        Updated = updated;
        FileModifiedUtc = fileModifiedUtc;
        FileCreatedUtc = fileCreatedUtc;
        Summary = summary;
        Keywords = keywords;
        Connections = connections;
        Body = body;
        SourcePaths = [FullPath];
    }

    public Guid SeriesId { get; }
    public string VersionKey { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public string FileName { get; }
    public string Title { get; }
    public DateOnly Created { get; }
    public DateOnly Updated { get; }
    public DateTime FileModifiedUtc { get; }
    public DateTime FileCreatedUtc { get; }
    public string Summary { get; }
    public List<string> Keywords { get; }
    public List<KnowledgeConnection> Connections { get; }
    public string Body { get; }
    public List<string> SourcePaths { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed record KnowledgeConnection(Guid? Id, string Reason);
public sealed record KnowledgeSeries(Guid Id, KnowledgeVersion CurrentVersion, List<KnowledgeVersion> HistoricalVersions);
public sealed record AmbiguousKnowledgeSeries(Guid Id, DateOnly LatestDate, List<KnowledgeVersion> Candidates);
public sealed record KnowledgeScanIssue(string Path, string Message);
public sealed record DownloadedFile(string FileName, byte[] Content);
public sealed record DeleteKnowledgeRequest(List<string>? VersionKeys);
public sealed record KnowledgeFileOperationResult(string FileName, string? VersionKey, string Outcome, bool Succeeded, string Message);
public sealed record LibraryStatusResponse(string RootPath, bool IsAvailable, DateTimeOffset ScannedAt, int CurrentSeriesCount, int AmbiguousSeriesCount, int VersionCount, IReadOnlyList<KnowledgeScanIssue> Issues);
public sealed record KnowledgeListItemResponse(Guid Id, string? VersionKey, string Title, DateOnly Updated, string Summary, int VersionCount, IReadOnlyList<Guid> ConnectedIds, bool IsAmbiguous, string? Warning);
public sealed record KnowledgeVersionResponse(string VersionKey, Guid SeriesId, string Title, DateOnly Created, DateOnly Updated, string Summary, string FileName, bool IsCurrent);
public sealed record VersionDetailResponse(string VersionKey, Guid SeriesId, string Title, DateOnly Created, DateOnly Updated, string Summary, IReadOnlyList<string> Keywords, IReadOnlyList<KnowledgeConnection> Connections, string Body, bool IsCurrent);
public sealed record LibrarySnapshot(
    string RootPath,
    DateTimeOffset ScannedAt,
    bool IsAvailable,
    IReadOnlyList<KnowledgeSeries> Series,
    IReadOnlyList<AmbiguousKnowledgeSeries> AmbiguousSeries,
    IReadOnlyDictionary<string, KnowledgeVersion> VersionsByKey,
    IReadOnlyList<KnowledgeScanIssue> Issues)
{
    public static LibrarySnapshot Empty { get; } = new(string.Empty, DateTimeOffset.MinValue, false, [], [], new Dictionary<string, KnowledgeVersion>(), []);
}