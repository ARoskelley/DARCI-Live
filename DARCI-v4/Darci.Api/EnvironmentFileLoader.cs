namespace Darci.Api;

public static class EnvironmentFileLoader
{
    public static void Load(string startDirectory, params string[] fileNames)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || fileNames == null || fileNames.Length == 0)
        {
            return;
        }

        foreach (var file in EnumerateCandidateFiles(startDirectory, fileNames))
        {
            LoadFile(file);
        }
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string startDirectory, string[] fileNames)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            foreach (var fileName in fileNames)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var candidate = Path.Combine(dir.FullName, fileName);
                if (!visited.Add(candidate))
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }

            dir = dir.Parent;
        }
    }

    private static void LoadFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].Trim();
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = line[(idx + 1)..].Trim();
            value = TrimMatchingQuotes(value);

            var current = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(current))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            return value;
        }

        var first = value[0];
        var last = value[^1];
        var matchingDouble = first == '"' && last == '"';
        var matchingSingle = first == '\'' && last == '\'';
        if (matchingDouble || matchingSingle)
        {
            return value[1..^1];
        }

        return value;
    }
}
