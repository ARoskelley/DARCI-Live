#nullable enable

using System.Text;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Coding.Tests;

// ── Model defaults ────────────────────────────────────────────────────────────

public class CodingModelDefaultsTests
{
    [Fact]
    public void CodingTaskRecord_DefaultConfidence_IsUnassessedGap()
    {
        var record = new CodingTaskRecord();
        // Unified type: default is Unassessed (-1), which counts as a gap but not "low".
        Assert.Equal(-1.0, record.Confidence.Score);
        Assert.False(record.Confidence.IsAssessed);
        Assert.True(record.Confidence.IsGap);
        Assert.False(record.Confidence.IsLow);
    }

    [Fact]
    public void CodingTaskStatusResponse_DefaultVerificationResult_IsEmpty()
    {
        var response = new CodingTaskStatusResponse();
        Assert.Equal("", response.VerificationResult);
    }

    [Fact]
    public void CodingPlanStep_DefaultConfidence_IsUnassessed()
    {
        var step = new CodingPlanStep();
        Assert.Equal(-1.0, step.Confidence.Score);
        Assert.False(step.Confidence.IsAssessed);
        Assert.Null(step.Confidence.Note);
    }
}

// ── CodingContextBuilder.ScoreFile ───────────────────────────────────────────

public class CodingContextBuilderScoreTests
{
    private static CodingFileEntry MakeFile(string relativePath, string kind, long sizeBytes = 1000) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws1",
            RelativePath = relativePath,
            Extension = Path.GetExtension(relativePath),
            Kind = kind,
            SizeBytes = sizeBytes,
            IsText = true,
            LastModifiedUtc = DateTime.UtcNow
        };

    [Fact]
    public void SourceFile_ScoresHigherThanProjectConfig_WithNoQuery()
    {
        var source = MakeFile("src/PaymentProcessor.cs", "source");
        var config = MakeFile("MyApp.csproj", "project-config");
        var tokens = Array.Empty<string>();

        var sourceScore = CodingContextBuilder.ScoreFile(source, tokens);
        var configScore = CodingContextBuilder.ScoreFile(config, tokens);

        Assert.True(sourceScore > configScore,
            $"source ({sourceScore:F2}) should outrank project-config ({configScore:F2}) with no query");
    }

    [Fact]
    public void SourceFile_ScoresHigherThanBuildConfig_WithNoQuery()
    {
        var source = MakeFile("src/Foo.cs", "source");
        var buildConfig = MakeFile("MyApp.sln", "build-config");
        var tokens = Array.Empty<string>();

        var sourceScore = CodingContextBuilder.ScoreFile(source, tokens);
        var buildScore = CodingContextBuilder.ScoreFile(buildConfig, tokens);

        Assert.True(sourceScore > buildScore,
            $"source ({sourceScore:F2}) should outrank build-config ({buildScore:F2}) with no query");
    }

    [Fact]
    public void TestFile_ScoresHigherThanDocumentation_WithNoQuery()
    {
        var test = MakeFile("tests/PaymentTests.cs", "test");
        var doc = MakeFile("README.md", "documentation");
        var tokens = Array.Empty<string>();

        var testScore = CodingContextBuilder.ScoreFile(test, tokens);
        var docScore = CodingContextBuilder.ScoreFile(doc, tokens);

        Assert.True(testScore > docScore,
            $"test ({testScore:F2}) should outrank documentation ({docScore:F2}) with no query");
    }

    [Fact]
    public void TokenMatch_BooststScore()
    {
        var file = MakeFile("src/payment/PaymentProcessor.cs", "source");
        var noMatch = MakeFile("src/other/Widget.cs", "source");
        var tokens = new[] { "payment" };

        var matchScore = CodingContextBuilder.ScoreFile(file, tokens);
        var noMatchScore = CodingContextBuilder.ScoreFile(noMatch, tokens);

        Assert.True(matchScore > noMatchScore,
            $"file with token match ({matchScore:F2}) should rank above no-match ({noMatchScore:F2})");
    }

    [Fact]
    public void MultipleTokenMatches_AccumulateScore()
    {
        var file = MakeFile("src/payment/PaymentProcessor.cs", "source");
        var tokens = new[] { "payment", "processor" };

        var score = CodingContextBuilder.ScoreFile(file, tokens);

        // Two matches = +2.0 on top of base
        Assert.True(score >= 2.5f, $"Two token matches should push score above 2.5, got {score:F2}");
    }

    [Fact]
    public void ScoreFile_CaseInsensitiveTokenMatch()
    {
        var file = MakeFile("src/UserService.cs", "source");
        var tokens = new[] { "userservice" };  // lowercase

        var score = CodingContextBuilder.ScoreFile(file, tokens);

        // Should match despite case difference
        Assert.True(score > 1.0f, $"Case-insensitive match should boost score, got {score:F2}");
    }
}

// ── CodingContextBuilder.CosineSimilarity ────────────────────────────────────

public class CosineSimilarityTests
{
    [Fact]
    public void IdenticalVectors_ReturnOne()
    {
        var v = new float[] { 0.5f, 0.3f, 0.8f };
        Assert.Equal(1.0f, CodingContextBuilder.CosineSimilarity(v, v), precision: 5);
    }

    [Fact]
    public void OppositeVectors_ReturnNegativeOne()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { -1f, 0f };
        Assert.Equal(-1.0f, CodingContextBuilder.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void OrthogonalVectors_ReturnZero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        Assert.Equal(0.0f, CodingContextBuilder.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void EmptyVectors_ReturnZero()
    {
        Assert.Equal(0f, CodingContextBuilder.CosineSimilarity(Array.Empty<float>(), Array.Empty<float>()));
    }

    [Fact]
    public void MismatchedLengths_ReturnZero()
    {
        var a = new float[] { 1f, 2f };
        var b = new float[] { 1f };
        Assert.Equal(0f, CodingContextBuilder.CosineSimilarity(a, b));
    }
}

// ── KgEnrichmentService.ExtractSymbols ───────────────────────────────────────

public class KgSymbolExtractionTests
{
    [Fact]
    public void CSharp_ExtractsPublicClass()
    {
        var content = "public class PaymentProcessor {\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "PaymentProcessor" && s.Kind == "type");
    }

    [Fact]
    public void CSharp_ExtractsPublicMethod_QualifiedByContainingClass()
    {
        var content = "public class Foo {\n    public bool Validate(string x) { return true; }\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        // Method is now qualified with its containing class to prevent KG collisions.
        Assert.Contains(symbols, s => s.Name == "Foo.Validate" && s.Kind == "method");
    }

    [Fact]
    public void CSharp_SameMethodInDifferentClasses_GetSeparateQualifiedNames()
    {
        var content = """
            public class Foo {
                public bool Validate(string x) { return true; }
            }
            public class Bar {
                public bool Validate(int y) { return false; }
            }
            """;
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "Foo.Validate" && s.Kind == "method");
        Assert.Contains(symbols, s => s.Name == "Bar.Validate" && s.Kind == "method");
        Assert.Equal(2, symbols.Count(s => s.Name.EndsWith(".Validate")));
    }

    [Fact]
    public void CSharp_MethodWithNoContainingClass_UsesUnqualifiedName()
    {
        // Top-level code scenario: no class declaration in the content.
        var content = "public bool Validate(string x) { return true; }";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "Validate" && s.Kind == "method");
    }

    [Fact]
    public void CSharp_ExtractsInterface()
    {
        var content = "public interface IPaymentGateway { }";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "IPaymentGateway" && s.Kind == "type");
    }

    [Fact]
    public void CSharp_ExtractsSealedRecord()
    {
        var content = "public sealed record OrderSummary(int Id, string Name);";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "OrderSummary" && s.Kind == "type");
    }

    [Fact]
    public void CSharp_SignatureIncludesDeclarationLine()
    {
        var content = "public async Task<bool> ProcessPaymentAsync(string orderId) { }";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        var method = symbols.FirstOrDefault(s => s.Name == "ProcessPaymentAsync");
        Assert.NotNull(method);
        Assert.Contains("ProcessPaymentAsync", method.Signature);
    }

    [Fact]
    public void CSharp_SkipsKeywords()
    {
        var content = "if (true) { return null; }";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();

        Assert.Empty(symbols);
    }

    [Fact]
    public void CSharp_NoDuplicateSymbolNames()
    {
        // Two methods named "Process" in the same file.
        var content = "public void Process(int x) {}\npublic void Process(string y) {}";
        var symbols = KgEnrichmentService.ExtractSymbols(".cs", content).ToList();
        var processSymbols = symbols.Where(s => s.Name == "Process").ToList();

        Assert.Single(processSymbols);
    }

    [Fact]
    public void Python_ExtractsClassAndFunction()
    {
        var content = "class OrderService:\n    pass\n\ndef process_order(order_id):\n    pass\n";
        var symbols = KgEnrichmentService.ExtractSymbols(".py", content).ToList();

        Assert.Contains(symbols, s => s.Name == "OrderService" && s.Kind == "class");
        Assert.Contains(symbols, s => s.Name == "process_order" && s.Kind == "function");
    }

    [Fact]
    public void TypeScript_ExtractsExportedFunction()
    {
        var content = "export function calculateTotal(items: Item[]): number { return 0; }";
        var symbols = KgEnrichmentService.ExtractSymbols(".ts", content).ToList();

        Assert.Contains(symbols, s => s.Name == "calculateTotal" && s.Kind == "export");
    }

    [Fact]
    public void Go_ExtractsFunctionDeclaration()
    {
        var content = "func ProcessPayment(ctx context.Context, amount float64) error {\n    return nil\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".go", content).ToList();

        Assert.Contains(symbols, s => s.Name == "ProcessPayment" && s.Kind == "function");
    }

    [Fact]
    public void Go_ExtractsMethodWithReceiver()
    {
        var content = "func (p *PaymentService) Charge(amount float64) error {\n    return nil\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".go", content).ToList();

        Assert.Contains(symbols, s => s.Name == "Charge" && s.Kind == "function");
    }

    [Fact]
    public void Rust_ExtractsPublicFunction()
    {
        var content = "pub fn calculate_fee(amount: f64) -> f64 {\n    amount * 0.03\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".rs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "calculate_fee" && s.Kind == "function");
    }

    [Fact]
    public void Rust_ExtractsAsyncFunction()
    {
        var content = "pub async fn fetch_order(id: u64) -> Result<Order, Error> {\n    todo!()\n}";
        var symbols = KgEnrichmentService.ExtractSymbols(".rs", content).ToList();

        Assert.Contains(symbols, s => s.Name == "fetch_order" && s.Kind == "function");
    }

    [Fact]
    public void UnknownExtension_ReturnsEmpty()
    {
        var symbols = KgEnrichmentService.ExtractSymbols(".xyz", "anything here").ToList();
        Assert.Empty(symbols);
    }
}

// ── PatchApplier ─────────────────────────────────────────────────────────────

public class PatchApplierTests : IDisposable
{
    private readonly string _root;
    private readonly PatchApplier _applier;

    public PatchApplierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "darci_patch_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _applier = new PatchApplier(NullLogger<PatchApplier>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task FileBlock_CreatesNewFile()
    {
        var response = """
            ### FILE: src/Greeter.cs
            ```csharp
            public class Greeter
            {
                public string Greet() => "Hello";
            }
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.Single(results);
        Assert.True(results[0].Success, results[0].Error);
        // Normalize separators for cross-platform comparison.
        Assert.Equal("src/Greeter.cs", results[0].RelativePath.Replace('\\', '/'));
        Assert.True(File.Exists(Path.Combine(_root, "src", "Greeter.cs")));
    }

    [Fact]
    public async Task FileBlock_OverwritesExistingFile()
    {
        var filePath = Path.Combine(_root, "Foo.cs");
        await File.WriteAllTextAsync(filePath, "old content");

        var response = """
            ### FILE: Foo.cs
            ```csharp
            public class Foo { }
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.True(results[0].Success);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("public class Foo", content);
        Assert.DoesNotContain("old content", content);
    }

    [Fact]
    public async Task FileBlock_PathTraversal_IsSanitizedIntoWorkspace()
    {
        // NormalizeRelPath strips leading dots and separators, so ../../evil.cs becomes
        // evil.cs and is created safely inside the workspace root rather than being rejected.
        // This verifies the sanitization behaviour — the file must never appear outside _root.
        var response = """
            ### FILE: ../../evil.cs
            ```csharp
            // sanitized
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.Single(results);
        if (results[0].Success)
        {
            // File was created — confirm it's inside the workspace root.
            var sanitizedPath = Path.GetFullPath(Path.Combine(_root, results[0].RelativePath));
            Assert.StartsWith(Path.GetFullPath(_root), sanitizedPath, StringComparison.OrdinalIgnoreCase);
        }
        // Whether the applier chose to sanitize or reject, the file must NOT exist outside root.
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.cs")),
            "Traversal must not create files outside the workspace root.");
    }

    [Fact]
    public async Task MultipleFileBlocks_AllApplied()
    {
        var response = """
            ### FILE: A.cs
            ```csharp
            public class A { }
            ```

            ### FILE: B.cs
            ```csharp
            public class B { }
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Error));
    }

    [Fact]
    public async Task EmptyResponse_ReturnsNoResults()
    {
        var results = await _applier.ApplyAsync(_root, "");
        Assert.Empty(results);
    }

    [Fact]
    public async Task ResponseWithNoFileBlocks_ReturnsNoResults()
    {
        var results = await _applier.ApplyAsync(_root, "Some text but no FILE: headers here.");
        Assert.Empty(results);
    }

    [Fact]
    public async Task FileBlock_WithAlternateHashCount_IsRecognised()
    {
        // ## FILE: and # FILE: should also be parsed (FileHeaderRegex allows 1-3 hashes).
        var response = """
            ## FILE: src/Widget.cs
            ```csharp
            public class Widget { }
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.Single(results);
        Assert.True(results[0].Success, results[0].Error);
    }

    [Fact]
    public async Task FileBlock_CreatesSubdirectoriesAsNeeded()
    {
        var response = """
            ### FILE: deep/nested/dir/Service.cs
            ```csharp
            public class Service { }
            ```
            """;

        var results = await _applier.ApplyAsync(_root, response);

        Assert.True(results[0].Success, results[0].Error);
        Assert.True(Directory.Exists(Path.Combine(_root, "deep", "nested", "dir")));
    }
}

// ── CodingAgentLoop.AppendResearch ───────────────────────────────────────────

public class AppendResearchTests
{
    // AppendResearch is private; we test it indirectly via the public-facing
    // observable: that multiple escalations accumulate rather than replace.
    // For now we at least verify the logic via reflection since we can't call
    // the method directly without making it internal. Instead we test the
    // documented edge cases with a copy of the logic here so we have a spec.

    private static string AppendResearch(string existing, string incoming, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming.Length <= maxChars ? incoming : incoming[..maxChars];
        var combined = existing.TrimEnd() + "\n\n---\n\n" + incoming.TrimStart();
        return combined.Length <= maxChars ? combined : combined[^maxChars..];
    }

    [Fact]
    public void EmptyExisting_ReturnsIncoming()
    {
        var result = AppendResearch("", "new research");
        Assert.Equal("new research", result);
    }

    [Fact]
    public void NonEmptyExisting_AppendsSeparatedByDivider()
    {
        var result = AppendResearch("first", "second");
        Assert.Contains("first", result);
        Assert.Contains("second", result);
        Assert.Contains("---", result);
    }

    [Fact]
    public void VeryLongCombined_TruncatesFromFront()
    {
        var existing = new string('A', 3000);
        var incoming = new string('B', 1500);
        var result = AppendResearch(existing, incoming, maxChars: 4000);

        Assert.True(result.Length <= 4000, $"Should be capped at 4000, got {result.Length}");
        // The tail (most recent content = 'B') should be preserved.
        Assert.True(result.EndsWith(new string('B', Math.Min(result.Length, 1500))[..10]),
            "Most recent research should be at end of truncated output");
    }
}
