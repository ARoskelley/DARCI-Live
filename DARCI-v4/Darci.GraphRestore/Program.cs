#nullable enable

using Darci.Memory.Graph;

// ── Darci.GraphRestore ───────────────────────────────────────────────────────────────────────────────
//
// Rebuilds the Neo4j knowledge graph from SQLite, which is the canonical store: kg_entities and
// kg_relations live in darci.db, and Neo4j is a DERIVED copy (P2d). KnowledgeGraphMigrator has always
// been able to do this, but it had no call site outside its own tests — so when the Neo4j copy was lost
// to an accidental test wipe there was no way to run the recovery that already existed. This is that
// call site.
//
// The copy is idempotent: it MERGEs by id and never deletes, so re-running is safe and a partial run can
// simply be repeated. Entity and relation IDs are preserved, which is load-bearing — entity confidence
// is derived by matching ids in confidence_claims, which stays in SQLite.
//
// Defaults to VERIFY-ONLY. Writing needs --apply, so the accident mode of this tool is "told you what it
// would do", not "changed your graph".
//
//   dotnet run --project DARCI-v4/Darci.GraphRestore -- --env-file DARCI-v4/Darci.Api/.env.local
//   dotnet run --project DARCI-v4/Darci.GraphRestore -- --env-file ... --apply

var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
var envFile = ArgValue("--env-file") ?? DefaultEnvFile();
if (envFile is not null && File.Exists(envFile))
{
    LoadEnvFile(envFile);
    Console.WriteLine($"env file   : {envFile}");
}
else
{
    Console.WriteLine("env file   : (none — relying on the process environment)");
}

var neo4j = Neo4jOptions.FromEnvironment();
if (!neo4j.IsConfigured)
{
    Console.Error.WriteLine(
        "DARCI_NEO4J_PASSWORD is not set, so there is no Neo4j to restore INTO. Point --env-file at the "
        + "host's .env.local, or export the DARCI_NEO4J_* variables.");
    return 2;
}

var dbPath = ResolveDbPath();
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"SQLite database not found at '{dbPath}' — nothing to restore FROM.");
    return 2;
}

// Entity confidence is read from confidence_claims in the same SQLite file, so the target options carry
// that connection string too; without it a restored entity reads back with a different confidence than
// the one it was migrated with.
var sqliteConn = $"Data Source={dbPath}";
var options = neo4j with { ClaimsConnectionString = sqliteConn };

Console.WriteLine($"sqlite     : {dbPath}");
Console.WriteLine($"neo4j      : {options.Uri} (database '{options.Database}', user '{options.User}')");
Console.WriteLine($"mode       : {(apply ? "APPLY — will write to Neo4j" : "VERIFY-ONLY (pass --apply to write)")}");
Console.WriteLine();

var migrator = new KnowledgeGraphMigrator(sqliteConn, options);
var report = apply ? await migrator.MigrateAsync() : await migrator.VerifyAsync();

Console.WriteLine($"source     : {report.SourceEntities} entity(ies), {report.SourceRelations} relation(s)   [SQLite]");
Console.WriteLine($"target     : {report.TargetEntities} entity(ies), {report.TargetRelations} relation(s)   [Neo4j]");
Console.WriteLine($"field-level: {(report.IsClean ? "CLEAN — every field compared equal" : $"{report.Mismatches.Count} mismatch(es)")}");

foreach (var m in report.Mismatches.Take(20)) Console.WriteLine($"  - {m}");
if (report.Mismatches.Count > 20) Console.WriteLine($"  … and {report.Mismatches.Count - 20} more");

// A clean verify means every SQLite row was found in Neo4j with matching fields. Equal counts on top of
// that mean Neo4j holds nothing extra either.
var counts = report.TargetEntities == report.SourceEntities && report.TargetRelations == report.SourceRelations;
Console.WriteLine();
Console.WriteLine(report.IsClean && counts
    ? "RESULT     : graphs agree."
    : "RESULT     : graphs DISAGREE — see above." + (apply ? "" : " Re-run with --apply to copy SQLite into Neo4j."));

return report.IsClean && counts ? 0 : 1;

string? ArgValue(string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Walks up for the repo root so the tool works from any working directory.
string? DefaultEnvFile()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "DARCI-v4", "Darci.Api", ".env.local");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

// Same precedence the API uses: a variable already present in the environment WINS over the file.
void LoadEnvFile(string path)
{
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line["export ".Length..].Trim();

        var idx = line.IndexOf('=');
        if (idx <= 0) continue;

        var key = line[..idx].Trim();
        var value = line[(idx + 1)..].Trim().Trim('"').Trim('\'');
        if (key.Length == 0) continue;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))) continue;
        Environment.SetEnvironmentVariable(key, value);
    }
}

// Mirrors Darci.Api's resolution, including its default, so the tool reads the same database the core does.
string ResolveDbPath()
{
    var cli = ArgValue("--db");
    if (!string.IsNullOrWhiteSpace(cli)) return Path.GetFullPath(cli);

    var configured = Environment.GetEnvironmentVariable("DARCI_DB_PATH");
    if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "DARCI-v4", "Data", "darci.db");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine("DARCI-v4", "Data", "darci.db"));
}
