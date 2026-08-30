#nullable enable

using System.IO.Compression;
using DarciControl.Logic.Prerequisites;

namespace DarciControl.Logic.Packaging;

/// <summary>Outcome of writing a zip.</summary>
public sealed record ZipBuildResult(
    bool Success,
    string ZipPath,
    long Bytes,
    IReadOnlyList<string> IncludedNodeIds,
    IReadOnlyList<string> Warnings,
    string? Error = null);

/// <summary>
/// Writes the zip a <see cref="ZipPlan"/> describes. Thin on purpose — every decision worth testing was
/// already made in the plan, so this is copying and a refusal.
/// </summary>
public static class ZipAssembler
{
    /// <summary>Directories never copied into the zip: build noise and per-machine state.</summary>
    private static readonly string[] ExcludedDirectories = { "bin", "obj", ".git", "Data", "Workspaces" };

    public static ZipBuildResult Write(ZipPlan plan, string outputPath, string readme)
    {
        // REFUSE rather than filter. If a secret reached the plan, the plan is wrong, and quietly dropping
        // the file would hide that from the next person who changes it.
        var forbidden = plan.FindForbidden();
        if (forbidden.Count > 0)
        {
            return new ZipBuildResult(false, outputPath, 0, plan.IncludedNodeIds, plan.Warnings,
                $"Refusing to package: {string.Join(", ", forbidden)} must never ship in a distributable.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                foreach (var entry in plan.Entries)
                {
                    if (entry.IsDirectory) AddDirectory(archive, entry.Source, entry.EntryPath);
                    else if (File.Exists(entry.Source)) archive.CreateEntryFromFile(entry.Source, entry.EntryPath);
                }

                WriteText(archive, "darci/README.md", readme);

                // Generated for THIS layout — the repo launcher cannot work here. See PackagedStartScript.
                WriteText(archive, $"darci/{PackagedStartScript.FileName}", PackagedStartScript.Build());
            }

            return new ZipBuildResult(true, outputPath, new FileInfo(outputPath).Length,
                plan.IncludedNodeIds, plan.Warnings);
        }
        catch (Exception ex)
        {
            return new ZipBuildResult(false, outputPath, 0, plan.IncludedNodeIds, plan.Warnings,
                ex.GetBaseException().Message);
        }
    }

    private static void WriteText(ZipArchive archive, string entryPath, string content)
    {
        var entry = archive.CreateEntry(entryPath);

        // A BOM for scripts, deliberately. Windows PowerShell 5.1 reads a .ps1 as ANSI unless one is
        // present, so BOM-less UTF-8 turns any non-ASCII character into mojibake — and mojibake inside a
        // script is not a cosmetic problem, it is a parse error on the recipient's machine. Found by
        // running a generated launcher rather than by asserting on its text.
        var needsBom = entryPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: needsBom));
        writer.Write(content);
    }

    private static void AddDirectory(ZipArchive archive, string sourceDir, string entryPrefix)
    {
        if (!Directory.Exists(sourceDir)) return;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');

            if (relative.Split('/').Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                continue;

            // Belt and braces: a secret must not slip in via a directory copy either.
            if (ZipPlan.ForbiddenNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                continue;

            archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}");
        }
    }

    /// <summary>
    /// The README the recipient actually reads. Generated rather than templated from a file so it always
    /// names the models THIS zip's host profile requires — a static README is precisely how
    /// "ollama pull gemma4:e4b" survived for months.
    /// </summary>
    public static string BuildReadme(ZipBuildRequest request, ZipPlan plan, string? hostProfilePath)
    {
        var models = RequiredModels.FromFileOrDefaults(hostProfilePath);
        var nodes = plan.IncludedNodeIds.Count == 0
            ? "_None — this is a bare core._ It runs, and honestly reports that no capabilities are available."
            : string.Join("\n", plan.IncludedNodeIds.Select(n => $"- `{n}`"));

        return $"""
        # DARCI

        A self-contained DARCI core. The .NET runtime is bundled — you do **not** need the .NET SDK.

        ## What you need first

        **Ollama** is the one external dependency and is not bundled (it ships its own models and
        installer). Install it from <https://ollama.com>, then pull the models this build expects:

        ```
        {string.Join("\n", models.Select(m => $"ollama pull {m}"))}
        ```

        Those names come from `host-profile.json` in this zip. If you edit that file to use different
        models, pull those instead — the profile is the single source of truth, and `Start-DARCI.ps1`
        derives its checks from it rather than from a hardcoded list.

        ## Running it

        ```
        .\Start-DARCI.ps1
        ```

        It checks prerequisites, starts the core on <http://localhost:5081>, and opens the web UI at
        <http://localhost:5081/app/>.

        ## What is included

        ### Nodes
        {nodes}

        A node that is not included is not merely disabled — it is absent. Requests for a capability
        nothing serves terminate honestly as *blocked* and are recorded as a gap, rather than failing.

        ### Knowledge graph
        Runs on **SQLite** out of the box, with no external database. Neo4j is optional: copy
        `.env.local.example` to `.env.local` and set `DARCI_NEO4J_PASSWORD` only if you have Neo4j
        running. If it is configured but unreachable, the core logs a warning and falls back to SQLite
        rather than failing to start.

        ### Neural decision models
        {(request.IncludeOnnxModels
            ? "Included. The core uses its trained ONNX policy for decisions."
            : "**Not included** (they are large and distributed separately). The core falls back to its priority ladder — it works, with simpler decision-making.")}

        ## First run

        The database is created automatically on first start; there is nothing to import. Your data stays
        in this folder.

        ## Configuration

        Everything optional lives in `.env.local` (copy from `.env.local.example`). No secret is included
        in this zip — you supply your own.
        """;
    }
}
