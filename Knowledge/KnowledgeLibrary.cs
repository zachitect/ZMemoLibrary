using System.Security.Cryptography;

public sealed class KnowledgeLibrary
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<KnowledgeLibrary> _logger;
    private readonly object _lock = new();
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;

    public KnowledgeLibrary(IConfiguration configuration, ILogger<KnowledgeLibrary> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void Rescan()
    {
        var configuredRoot = _configuration["KnowledgeLibrary:RootPath"];
        var rootPath = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath("KnowledgeFiles")
            : Path.GetFullPath(configuredRoot);

        var scannedAt = DateTimeOffset.UtcNow;
        if (!Directory.Exists(rootPath))
        {
            var issue = new KnowledgeScanIssue(rootPath, $"Knowledge root does not exist: {rootPath}");
            lock (_lock)
                _snapshot = new LibrarySnapshot(rootPath, scannedAt, false, [], [], new Dictionary<string, KnowledgeVersion>(), [issue]);
            _logger.LogWarning("Knowledge root does not exist: {RootPath}", rootPath);
            return;
        }

        var issues = new List<KnowledgeScanIssue>();
        var parsedVersions = new List<KnowledgeVersion>();

        var files = EnumerateMarkdownFiles(rootPath, issues);

        foreach (var filePath in files)
        {
            try
            {
                parsedVersions.Add(KnowledgeFileParser.Parse(filePath, rootPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                issues.Add(new KnowledgeScanIssue(Path.GetRelativePath(rootPath, filePath), ex.Message));
            }
        }

        var versionsByKey = new Dictionary<string, KnowledgeVersion>(StringComparer.OrdinalIgnoreCase);
        var currentSeries = new List<KnowledgeSeries>();
        var ambiguousSeries = new List<AmbiguousKnowledgeSeries>();

        foreach (var group in parsedVersions.GroupBy(version => version.SeriesId))
        {
            var distinctVersions = new List<KnowledgeVersion>();
            foreach (var hashGroup in group.GroupBy(version => version.VersionKey, StringComparer.OrdinalIgnoreCase))
            {
                var copies = hashGroup
                    .OrderByDescending(version => version.FileModifiedUtc)
                    .ThenByDescending(version => version.FileCreatedUtc)
                    .ThenBy(version => version.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var retained = copies[0];
                distinctVersions.Add(retained);
                versionsByKey[retained.VersionKey] = retained;

                if (copies.Count > 1)
                {
                    issues.Add(new KnowledgeScanIssue(
                        string.Join("; ", copies.Select(copy => copy.RelativePath)),
                        $"Collapsed {copies.Count} byte-identical copies of version {retained.VersionKey}."));
                }
            }

            var latestDate = distinctVersions.Max(version => version.Updated);
            var latestCandidates = distinctVersions.Where(version => version.Updated == latestDate).ToList();
            var latestModifiedUtc = latestCandidates.Max(version => version.FileModifiedUtc);
            latestCandidates = latestCandidates.Where(version => version.FileModifiedUtc == latestModifiedUtc).ToList();
            var latestCreatedUtc = latestCandidates.Max(version => version.FileCreatedUtc);
            latestCandidates = latestCandidates.Where(version => version.FileCreatedUtc == latestCreatedUtc).ToList();

            if (latestCandidates.Count > 1)
            {
                var orderedCandidates = latestCandidates.OrderBy(version => version.Title, StringComparer.OrdinalIgnoreCase).ToList();
                ambiguousSeries.Add(new AmbiguousKnowledgeSeries(group.Key, latestDate, orderedCandidates));
                issues.Add(new KnowledgeScanIssue(
                    group.Key.ToString(),
                    $"Current version is ambiguous: {latestCandidates.Count} distinct files share UPDATED {latestDate:yyyy-MM-dd}, modified time {latestModifiedUtc:O}, and creation time {latestCreatedUtc:O}."));
                continue;
            }

            var current = latestCandidates[0];
            current.IsCurrent = true;
            var history = distinctVersions
                .Where(version => !ReferenceEquals(version, current))
                .OrderByDescending(version => version.Updated)
                .ThenByDescending(version => version.FileModifiedUtc)
                .ThenByDescending(version => version.FileCreatedUtc)
                .ThenBy(version => version.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            currentSeries.Add(new KnowledgeSeries(group.Key, current, history));
        }

        currentSeries = currentSeries
            .OrderByDescending(series => series.CurrentVersion.Updated)
            .ThenBy(series => series.CurrentVersion.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ambiguousSeries = ambiguousSeries
            .OrderByDescending(series => series.LatestDate)
            .ThenBy(series => series.Id)
            .ToList();

        lock (_lock)
            _snapshot = new LibrarySnapshot(rootPath, scannedAt, true, currentSeries, ambiguousSeries, versionsByKey, issues);

        _logger.LogInformation(
            "Scanned {FileCount} files into {SeriesCount} current knowledge series with {IssueCount} issues.",
            parsedVersions.Count,
            currentSeries.Count,
            issues.Count);
    }

    public LibraryStatusResponse GetStatus()
    {
        var snapshot = GetSnapshot();
        return new LibraryStatusResponse(
            snapshot.RootPath,
            snapshot.IsAvailable,
            snapshot.ScannedAt,
            snapshot.Series.Count,
            snapshot.AmbiguousSeries.Count,
            snapshot.VersionsByKey.Count,
            snapshot.Issues);
    }

    public IReadOnlyList<KnowledgeListItemResponse> Search(string? query)
    {
        var snapshot = GetSnapshot();
        if (!snapshot.IsAvailable)
            return [];

        var terms = SplitSearchTerms(query);
        var results = new List<(KnowledgeListItemResponse Item, int Score)>();

        foreach (var series in snapshot.Series)
        {
            var version = series.CurrentVersion;
            var score = CalculateScore(version, terms);
            if (score < 0)
                continue;

            results.Add((new KnowledgeListItemResponse(
                series.Id,
                version.VersionKey,
                version.Title,
                version.Updated,
                version.Summary,
                series.HistoricalVersions.Count + 1,
                version.Connections.Where(connection => connection.Id.HasValue).Select(connection => connection.Id!.Value).Distinct().ToList(),
                IsAmbiguous: false,
                Warning: null), score));
        }

        if (terms.Count == 0)
        {
            foreach (var series in snapshot.AmbiguousSeries)
            {
                results.Add((new KnowledgeListItemResponse(
                    series.Id,
                    null,
                    $"Ambiguous current version ({series.Candidates.Count} candidates)",
                    series.LatestDate,
                    "This series is excluded from search, copying, and selected-file downloads until its current-version conflict is resolved.",
                    series.Candidates.Count,
                    [],
                    IsAmbiguous: true,
                    Warning: $"{series.Candidates.Count} distinct files share UPDATED {series.LatestDate:yyyy-MM-dd}."), int.MinValue));
            }
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Item.Updated)
            .ThenBy(result => result.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(result => result.Item)
            .ToList();
    }

    public IReadOnlyList<KnowledgeVersionResponse>? GetVersions(Guid id)
    {
        var snapshot = GetSnapshot();
        var series = snapshot.Series.FirstOrDefault(item => item.Id == id);
        if (series != null)
        {
            var versions = new List<KnowledgeVersion> { series.CurrentVersion };
            versions.AddRange(series.HistoricalVersions);
            return versions.Select(ToVersionResponse).ToList();
        }

        var ambiguous = snapshot.AmbiguousSeries.FirstOrDefault(item => item.Id == id);
        return ambiguous?.Candidates.Select(ToVersionResponse).ToList();
    }

    public KnowledgeVersion? GetVersion(string versionKey)
    {
        var snapshot = GetSnapshot();
        return snapshot.VersionsByKey.GetValueOrDefault(versionKey);
    }

    public DownloadedFile? ReadVersionFile(string versionKey)
    {
        var version = GetVersion(versionKey);
        return version == null ? null : ReadAndVerify(version);
    }

    private static List<string> EnumerateMarkdownFiles(string rootPath, List<KnowledgeScanIssue> issues)
    {
        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly));
                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                    pendingDirectories.Push(childDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new KnowledgeScanIssue(
                    Path.GetRelativePath(rootPath, directory),
                    $"Could not scan directory: {ex.Message}"));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private LibrarySnapshot GetSnapshot()
    {
        lock (_lock)
            return _snapshot;
    }

    private static DownloadedFile ReadAndVerify(KnowledgeVersion version)
    {
        var content = File.ReadAllBytes(version.FullPath);
        var currentKey = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(currentKey, version.VersionKey, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The source file changed after the last scan: {version.RelativePath}. Rescan and try again.");

        return new DownloadedFile(version.FileName, content);
    }

    private static List<string> SplitSearchTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CalculateScore(KnowledgeVersion version, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return 0;

        var keywords = string.Join(' ', version.Keywords);
        var searchable = string.Join("\n", version.Title, keywords, version.Summary, version.Body, version.SeriesId.ToString());
        if (terms.Any(term => !searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return -1;

        var score = 0;
        foreach (var term in terms)
        {
            if (version.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (version.Keywords.Any(keyword => keyword.Contains(term, StringComparison.OrdinalIgnoreCase)))
                score += 70;
            if (version.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 40;
            if (version.Body.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 10;
            if (version.SeriesId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 100;
        }

        return score;
    }

    private static KnowledgeVersionResponse ToVersionResponse(KnowledgeVersion version)
    {
        return new KnowledgeVersionResponse(
            version.VersionKey,
            version.SeriesId,
            version.Title,
            version.Created,
            version.Updated,
            version.Summary,
            version.FileName,
            version.IsCurrent);
    }
}