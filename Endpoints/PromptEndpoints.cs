using System.Reflection;

public static class PromptEndpoints
{
    private static readonly IReadOnlyDictionary<string, EmbeddedPrompt> Prompts = new Dictionary<string, EmbeddedPrompt>(StringComparer.OrdinalIgnoreCase)
    {
        ["create"] = new("ZMemoLibrary.Prompts.create-knowledge.md", "create-knowledge-prompt.md"),
        ["update"] = new("ZMemoLibrary.Prompts.update-knowledge.md", "update-knowledge-prompt.md"),
        ["connections"] = new("ZMemoLibrary.Prompts.find-connections.md", "review-connections-prompt.md")
    };

    public static void MapPromptEndpoints(this WebApplication app)
    {
        app.MapGet("/api/prompts/{promptKey}", GetPrompt);
        app.MapGet("/api/prompts/{promptKey}/download", DownloadPrompt);
    }

    private static IResult GetPrompt(string promptKey)
    {
        var prompt = ReadPrompt(promptKey);
        return prompt == null
            ? Results.NotFound()
            : Results.Text(prompt.Value.Content, "text/markdown; charset=utf-8");
    }

    private static IResult DownloadPrompt(string promptKey)
    {
        var prompt = ReadPrompt(promptKey);
        return prompt == null
            ? Results.NotFound()
            : Results.File(System.Text.Encoding.UTF8.GetBytes(prompt.Value.Content), "text/markdown; charset=utf-8", prompt.Value.FileName);
    }

    private static (string Content, string FileName)? ReadPrompt(string promptKey)
    {
        if (!Prompts.TryGetValue(promptKey, out var prompt))
            return null;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(prompt.ResourceName);
        if (stream == null)
            throw new InvalidOperationException($"Embedded prompt resource is missing: {prompt.ResourceName}");

        using var reader = new StreamReader(stream);
        return (reader.ReadToEnd(), prompt.FileName);
    }

    private sealed record EmbeddedPrompt(string ResourceName, string FileName);
}