using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public static class KnowledgeFileParser
{
    public const int MaximumFileSizeBytes = 10 * 1024 * 1024;

    private const string HeaderStart = "BEGIN_KNOWLEDGE_HEADER";
    private const string HeaderEnd = "END_KNOWLEDGE_HEADER";
    private static readonly string[] RequiredFields =
    [
        "PROJECT", "ID", "TITLE", "CREATED", "UPDATED", "SUMMARY", "KEYWORDS", "CONNECTIONS"
    ];

    public static KnowledgeVersion Parse(string filePath, string rootPath)
    {
        var file = new FileInfo(filePath);
        if (file.Length > MaximumFileSizeBytes)
            throw new InvalidDataException("File exceeds the 10 MiB knowledge file limit.");

        var rawBytes = File.ReadAllBytes(filePath);
        if (rawBytes.Length > MaximumFileSizeBytes)
            throw new InvalidDataException("File exceeds the 10 MiB knowledge file limit.");

        var fileModifiedUtc = file.LastWriteTimeUtc;
        var fileCreatedUtc = GetCreationTimeUtc(file);
        string rawText;
        try
        {
            rawText = new UTF8Encoding(false, true).GetString(rawBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("File is not valid UTF-8.", ex);
        }

        var normalized = rawText.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0] != HeaderStart)
            throw new InvalidDataException($"First line must be exactly {HeaderStart}.");

        var endIndex = Array.IndexOf(lines, HeaderEnd);
        if (endIndex < 0)
            throw new InvalidDataException($"Missing {HeaderEnd}.");
        if (endIndex != RequiredFields.Length + 1)
            throw new InvalidDataException("Knowledge header must contain exactly the required fields in the required order.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < RequiredFields.Length; index++)
        {
            var expectedField = RequiredFields[index];
            var line = lines[index + 1];
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
                throw new InvalidDataException($"Invalid {expectedField} header line.");

            var field = line[..separatorIndex];
            if (field != expectedField)
                throw new InvalidDataException($"Expected {expectedField} as header field {index + 1}, but found {field}.");

            values[field] = line[(separatorIndex + 1)..].Trim();
        }

        var project = values["PROJECT"];
        if (string.IsNullOrWhiteSpace(project))
            throw new InvalidDataException("PROJECT is required.");
        if (string.Equals(project, "Unassigned", StringComparison.OrdinalIgnoreCase) && project != "Unassigned")
            throw new InvalidDataException("The reserved project name must use the exact spelling Unassigned.");

        if (!Guid.TryParse(values["ID"], out var id))
            throw new InvalidDataException("ID must be a valid GUID.");
        if (!DateOnly.TryParseExact(values["CREATED"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
            throw new InvalidDataException("CREATED must use YYYY-MM-DD.");
        if (!DateOnly.TryParseExact(values["UPDATED"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            throw new InvalidDataException("UPDATED must use YYYY-MM-DD.");
        if (string.IsNullOrWhiteSpace(values["TITLE"]))
            throw new InvalidDataException("TITLE is required.");
        if (string.IsNullOrWhiteSpace(values["SUMMARY"]))
            throw new InvalidDataException("SUMMARY is required.");

        var keywords = values["KEYWORDS"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var connections = ParseConnections(values["CONNECTIONS"]);
        if (connections.Any(connection => connection.Id == id))
            throw new InvalidDataException("A knowledge file cannot connect to itself.");
        var bodyStartIndex = endIndex + 1;
        if (bodyStartIndex < lines.Length && lines[bodyStartIndex].Length == 0)
            bodyStartIndex++;
        var body = string.Join('\n', lines.Skip(bodyStartIndex));
        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        var versionKey = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        return new KnowledgeVersion(
            id,
            versionKey,
            Path.GetFullPath(filePath),
            relativePath,
            Path.GetFileName(filePath),
            project,
            values["TITLE"],
            created,
            updated,
            fileModifiedUtc,
            fileCreatedUtc,
            values["SUMMARY"],
            keywords,
            connections,
            body);
    }

    private static DateTime GetCreationTimeUtc(FileInfo file)
    {
        try
        {
            return file.CreationTimeUtc;
        }
        catch (PlatformNotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static List<KnowledgeConnection> ParseConnections(string value)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("CONNECTIONS is required.");

        var connections = new List<KnowledgeConnection>();
        foreach (var part in parts)
        {
            var separatorIndex = part.IndexOf('|');
            if (separatorIndex < 0)
                throw new InvalidDataException("Each CONNECTIONS item must use GUID | reason.");

            var idText = part[..separatorIndex].Trim();
            var reason = part[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException("Each connection requires a reason.");

            if (string.Equals(idText, "none", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 1)
                    throw new InvalidDataException("CONNECTIONS cannot mix none with GUID entries.");
                connections.Add(new KnowledgeConnection(null, reason));
                continue;
            }

            if (!Guid.TryParse(idText, out var connectionId))
                throw new InvalidDataException($"Invalid connection GUID: {idText}.");
            connections.Add(new KnowledgeConnection(connectionId, reason));
        }

        return connections;
    }
}