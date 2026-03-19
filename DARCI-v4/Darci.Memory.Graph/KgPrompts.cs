#nullable enable

namespace Darci.Memory.Graph;

public static class KgPrompts
{
    public const string EntityExtractionTemplate = """
You are an entity extraction assistant. Given the following text, identify:
1. All meaningful entities (genes, proteins, diseases, compounds, concepts, people, systems).
2. All relationships between those entities.
Respond ONLY with valid JSON in this exact schema, no markdown:
{
  "entities": [{"name":"...","type":"...","domain":"...","description":"..."}],
  "relations": [{"from":"...","relation":"...","to":"...","confidence":0.0}]
}
Text: {{MEMORY_CONTENT}}
""";

    public static string BuildExtractionPrompt(string memoryContent)
        => EntityExtractionTemplate.Replace("{{MEMORY_CONTENT}}", memoryContent);
}
