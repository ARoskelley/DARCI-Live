using Darci.Tools.Cad;
using Darci.Tools.Engineering.Providers;
using Darci.Tools.Ollama;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Darci.Tools.Engineering;

public class CadQueryEngineeringWorkbench : IEngineeringWorkbench
{
    private readonly ILogger<CadQueryEngineeringWorkbench> _logger;
    private readonly IOllamaClient _ollama;
    private readonly ICadBridge _cad;
    private readonly List<IEngineeringCadProvider> _providers;

    public CadQueryEngineeringWorkbench(
        ILogger<CadQueryEngineeringWorkbench> logger,
        IOllamaClient ollama,
        ICadBridge cad,
        IEnumerable<IEngineeringCadProvider>? providers = null)
    {
        _logger = logger;
        _ollama = ollama;
        _cad = cad;
        _providers = (providers ?? Array.Empty<IEngineeringCadProvider>())
            .Where(p => p.IsConfigured)
            .ToList();

        if (_providers.Count > 0)
        {
            _logger.LogInformation(
                "Engineering providers configured: {Providers}",
                string.Join(", ", _providers.Select(p => p.Name)));
        }
    }

    public async Task<EngineeringWorkbenchResult> Run(EngineeringWorkRequest request)
    {
        var result = new EngineeringWorkbenchResult();
        _logger.LogInformation("Engineering workbench request: {Description}", request.Description);

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            result.Error = "Engineering request description is empty.";
            return result;
        }

        var inferredPartType = InferPartType(request.Description, request.PartType);
        var maxIterations = Math.Clamp(request.MaxIterations, 1, 6);
        var script = request.ProviderOnly
            ? ""
            : TryGetFastDeterministicScript(
                request.Description,
                request.Dimensions,
                inferredPartType,
                request.Parameters);
        var usedDeterministicFallback = !string.IsNullOrWhiteSpace(script);
        var scriptSource = usedDeterministicFallback ? "deterministic:fast" : "";
        var runId = $"eng_{Guid.NewGuid():N}";
        var providerErrors = new List<string>();
        var providerAttempts = new List<EngineeringProviderAttempt>();

        for (var i = 0; i < maxIterations; i++)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                var providerResult = await TryGenerateScriptFromProviders(request);
                script = providerResult.Script;
                if (!string.IsNullOrWhiteSpace(providerResult.ProviderName))
                {
                    scriptSource = $"provider:{providerResult.ProviderName}";
                }
                providerErrors.AddRange(providerResult.Errors);
                providerAttempts.AddRange(providerResult.Attempts);
            }

            if (string.IsNullOrWhiteSpace(script))
            {
                if (request.ProviderOnly)
                {
                    result.Error = providerErrors.Count > 0
                        ? $"Provider-only mode failed: {string.Join("; ", providerErrors)}"
                        : "Provider-only mode failed: no provider returned a script.";
                    result.GenerationSource = "provider-only";
                    result.ProviderAttempts = providerAttempts;
                    return result;
                }

                var planPrompt = BuildCadPrompt(
                    request.Description,
                    request.Dimensions,
                    inferredPartType,
                    request.Parameters);
                var llmOutput = await _ollama.Generate(planPrompt);
                script = ExtractCadScript(llmOutput);
                script = NormalizeCadScript(script);
                if (string.IsNullOrWhiteSpace(script))
                {
                    script = BuildDeterministicFallbackScript(
                        request.Description,
                        request.Dimensions,
                        inferredPartType,
                        request.Parameters);
                    usedDeterministicFallback = true;
                    scriptSource = "deterministic:fallback";
                }
                else
                {
                    scriptSource = "llm";
                }
            }

            if (string.IsNullOrWhiteSpace(script))
            {
                result.Error = "Failed to generate a valid CadQuery script.";
                result.GenerationSource = string.IsNullOrWhiteSpace(scriptSource) ? "unknown" : scriptSource;
                result.ProviderAttempts = providerAttempts;
                return result;
            }

            var cadResult = await _cad.Generate(new CadGenerateRequest
            {
                Script = script,
                Filename = $"{runId}_step_v{i}.stl",
                Dimensions = request.Dimensions
            });

            result.CadResult = cadResult;
            result.FinalScript = script;
            result.GenerationSource = string.IsNullOrWhiteSpace(scriptSource) ? "unknown" : scriptSource;
            result.ProviderAttempts = providerAttempts;

            if (cadResult == null)
            {
                result.Error = "CAD engine unavailable.";
                return result;
            }

            var validationSummary = EvaluateToolValidation(
                cadResult,
                inferredPartType,
                request.Parameters,
                request.StrictToolValidation);
            result.ValidationSummary = validationSummary;

            if (cadResult.Success && validationSummary.Passed)
            {
                result.Success = true;
                if (usedDeterministicFallback)
                {
                    _logger.LogInformation("Engineering workbench succeeded using deterministic fallback.");
                }
                return result;
            }

            var feedbackPrompt = await _cad.GetFeedbackPrompt(request.Description, cadResult);
            feedbackPrompt = AppendToolValidationContext(
                feedbackPrompt,
                request.Description,
                validationSummary);
            if (string.IsNullOrWhiteSpace(feedbackPrompt))
            {
                result.Error = BuildValidationFailureMessage(cadResult, validationSummary);
                return result;
            }

            var revised = await _ollama.Generate(feedbackPrompt);
            var revisedScript = ExtractCadScript(revised);
            revisedScript = NormalizeCadScript(revisedScript);
            if (string.IsNullOrWhiteSpace(revisedScript) || revisedScript == script)
            {
                if (!usedDeterministicFallback)
                {
                    var providerRetry = await TryGenerateScriptFromProviders(request);
                    var providerRetryScript = providerRetry.Script;
                    if (!string.IsNullOrWhiteSpace(providerRetry.ProviderName))
                    {
                        scriptSource = $"provider:{providerRetry.ProviderName}";
                    }
                    providerErrors.AddRange(providerRetry.Errors);
                    providerAttempts.AddRange(providerRetry.Attempts);
                    if (!string.IsNullOrWhiteSpace(providerRetryScript) && providerRetryScript != script)
                    {
                        script = providerRetryScript;
                        continue;
                    }

                    if (!request.ProviderOnly)
                    {
                        var fallback = BuildDeterministicFallbackScript(
                            request.Description,
                            request.Dimensions,
                            inferredPartType,
                            request.Parameters);
                        if (!string.IsNullOrWhiteSpace(fallback) && fallback != script)
                        {
                            script = fallback;
                            usedDeterministicFallback = true;
                            scriptSource = "deterministic:fallback";
                            continue;
                        }
                    }
                }

                result.Error = BuildValidationFailureMessage(cadResult, validationSummary);
                return result;
            }

            script = revisedScript;
            scriptSource = "llm:revision";
        }

        result.Error = "Reached engineering iteration limit without a passing CAD output.";
        result.GenerationSource = string.IsNullOrWhiteSpace(scriptSource) ? "unknown" : scriptSource;
        result.ProviderAttempts = providerAttempts;
        return result;
    }

    private async Task<(string Script, string? ProviderName, List<string> Errors, List<EngineeringProviderAttempt> Attempts)> TryGenerateScriptFromProviders(EngineeringWorkRequest request)
    {
        var errors = new List<string>();
        var attempts = new List<EngineeringProviderAttempt>();
        if (_providers.Count == 0)
        {
            return ("", null, errors, attempts);
        }

        var inferredPartType = InferPartType(request.Description, request.PartType);
        var providerRequest = new EngineeringProviderRequest
        {
            Description = request.Description,
            PartType = inferredPartType,
            Parameters = request.Parameters,
            Dimensions = request.Dimensions
        };

        foreach (var provider in _providers)
        {
            var result = await provider.TryGenerateScript(providerRequest);
            if (result?.Success == true && !string.IsNullOrWhiteSpace(result.Script))
            {
                _logger.LogInformation("Engineering provider '{Provider}' produced CAD script.", provider.Name);
                var extracted = ExtractCadScript(result.Script);
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    extracted = result.Script;
                }

                var normalized = NormalizeCadScript(extracted);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    attempts.Add(new EngineeringProviderAttempt
                    {
                        Provider = provider.Name,
                        Success = true
                    });
                    return (normalized, provider.Name, errors, attempts);
                }

                attempts.Add(new EngineeringProviderAttempt
                {
                    Provider = provider.Name,
                    Success = false,
                    Error = "Provider response did not contain a usable script."
                });
                errors.Add($"{provider.Name}: unusable provider script.");
                continue;
            }

            if (result != null && !result.Success)
            {
                errors.Add($"{provider.Name}: {result.Error}");
                attempts.Add(new EngineeringProviderAttempt
                {
                    Provider = provider.Name,
                    Success = false,
                    Error = result.Error
                });
                _logger.LogDebug(
                    "Engineering provider '{Provider}' did not produce script: {Error}",
                    provider.Name,
                    result.Error);
                continue;
            }

            attempts.Add(new EngineeringProviderAttempt
            {
                Provider = provider.Name,
                Success = false,
                Error = "Provider not configured or returned no result."
            });
        }

        return ("", null, errors, attempts);
    }

    private static string BuildCadPrompt(
        string description,
        CadDimensionSpec? dimensions,
        string? partType,
        Dictionary<string, double>? parameters)
    {
        var dims = "";
        if (dimensions != null)
        {
            var entries = new List<string>();
            if (dimensions.LengthMm.HasValue) entries.Add($"length={dimensions.LengthMm}mm");
            if (dimensions.WidthMm.HasValue) entries.Add($"width={dimensions.WidthMm}mm");
            if (dimensions.HeightMm.HasValue) entries.Add($"height={dimensions.HeightMm}mm");
            if (entries.Count > 0) dims = "\nDimensions: " + string.Join(", ", entries);
        }

        var typeLine = string.IsNullOrWhiteSpace(partType) ? "" : $"\nPart type: {partType}";
        var paramLine = "";
        if (parameters != null && parameters.Count > 0)
        {
            var items = string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
            paramLine = $"\nParameters: {items}";
        }

        return $@"Create a CadQuery Python script for this engineering request.
Rules:
1) Output only Python code.
2) Import only cadquery as cq and math.
3) Assign final watertight solid to variable named result.
4) Use millimeters.

Request: {description}{dims}{typeLine}{paramLine}";
    }

    private static string ExtractCadScript(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "";
        }

        var trimmed = output.Trim();
        if (trimmed.Contains("```python", StringComparison.OrdinalIgnoreCase))
        {
            var start = trimmed.IndexOf("```python", StringComparison.OrdinalIgnoreCase) + "```python".Length;
            var end = trimmed.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
            {
                trimmed = trimmed[start..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }

        if (!trimmed.Contains("result", StringComparison.Ordinal) ||
            !trimmed.Contains("cq.", StringComparison.Ordinal))
        {
            return "";
        }

        return trimmed;
    }

    private static string NormalizeCadScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return "";
        }

        return script
            .Replace(".Circle(", ".circle(", StringComparison.Ordinal)
            .Replace(".Rect(", ".rect(", StringComparison.Ordinal)
            .Replace(".Box(", ".box(", StringComparison.Ordinal)
            .Replace("cq.Circle(", "cq.Workplane(\"XY\").circle(", StringComparison.Ordinal)
            .Replace("cq.Rect(", "cq.Workplane(\"XY\").rect(", StringComparison.Ordinal)
            .Replace("cq.Box(", "cq.Workplane(\"XY\").box(", StringComparison.Ordinal);
    }

    private static string BuildDeterministicFallbackScript(
        string description,
        CadDimensionSpec? dimensions,
        string? partType,
        Dictionary<string, double>? parameters)
    {
        var normalizedType = InferPartType(description, partType);
        var typed = TryBuildTypedPartScript(normalizedType, dimensions, parameters);
        if (!string.IsNullOrWhiteSpace(typed))
        {
            return typed;
        }

        var text = (description ?? "").ToLowerInvariant();
        return text.Contains("bracket", StringComparison.Ordinal)
            ? BuildBracketScript(dimensions)
            : BuildBoxScript(dimensions);
    }

    private static string TryGetFastDeterministicScript(
        string description,
        CadDimensionSpec? dimensions,
        string? partType,
        Dictionary<string, double>? parameters)
    {
        var normalizedType = InferPartType(description, partType);
        var typed = TryBuildTypedPartScript(normalizedType, dimensions, parameters);
        if (!string.IsNullOrWhiteSpace(typed))
        {
            return typed;
        }

        var text = (description ?? "").ToLowerInvariant();
        if (text.Contains("bracket", StringComparison.Ordinal) ||
            text.Contains("cube", StringComparison.Ordinal) ||
            text.Contains("box", StringComparison.Ordinal) ||
            text.Contains("block", StringComparison.Ordinal) ||
            text.Contains("plate", StringComparison.Ordinal) ||
            text.Contains("gear", StringComparison.Ordinal) ||
            text.Contains("shaft", StringComparison.Ordinal) ||
            text.Contains("bearing", StringComparison.Ordinal) ||
            text.Contains("pin", StringComparison.Ordinal) ||
            text.Contains("housing", StringComparison.Ordinal))
        {
            return BuildDeterministicFallbackScript(description ?? "", dimensions, normalizedType, parameters);
        }

        return "";
    }

    private static string TryBuildTypedPartScript(
        string? partType,
        CadDimensionSpec? dimensions,
        Dictionary<string, double>? parameters)
    {
        var kind = NormalizePartType(partType);
        return kind switch
        {
            "gear" => BuildGearScript(dimensions, parameters),
            "shaft" => BuildShaftScript(dimensions, parameters),
            "bearing" or "bearing-seat" => BuildBearingScript(dimensions, parameters),
            "pin" => BuildPinScript(dimensions, parameters),
            "housing" => BuildHousingScript(dimensions, parameters),
            "plate" => BuildPlateScript(dimensions, parameters),
            "bracket" => BuildBracketScript(dimensions),
            _ => ""
        };
    }

    private static string NormalizePartType(string? partType)
    {
        if (string.IsNullOrWhiteSpace(partType))
        {
            return "";
        }

        var normalized = partType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "axle" => "shaft",
            "axle-shaft" => "shaft",
            "driveshaft" => "shaft",
            "drive-shaft" => "shaft",
            "rod" => "shaft",
            "dowel" => "pin",
            "bushing" => "bearing",
            "gearbox" => "housing",
            "axlebox" => "housing",
            "axle-box" => "housing",
            "enclosure" => "housing",
            "case" => "housing",
            "mount" => "bracket",
            _ => normalized
        };
    }

    private static string InferPartType(string description, string? explicitPartType)
    {
        var normalizedExplicit = NormalizePartType(explicitPartType);
        if (!string.IsNullOrWhiteSpace(normalizedExplicit))
        {
            return normalizedExplicit;
        }

        var text = (description ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (ContainsAny(text, "housing", "axle box", "axlebox", "gearbox", "enclosure", "case"))
        {
            return "housing";
        }

        if (ContainsAny(text, "bearing", "bushing"))
        {
            return "bearing";
        }

        if (ContainsAny(text, "driveshaft", "drive shaft", "shaft", "axle", "spindle", "rod"))
        {
            return "shaft";
        }

        if (ContainsAny(text, "pin", "dowel", "retaining pin", "roll pin"))
        {
            return "pin";
        }

        if (ContainsAny(text, "gear", "sprocket", "toothed wheel"))
        {
            return "gear";
        }

        if (ContainsAny(text, "plate", "panel", "flange"))
        {
            return "plate";
        }

        if (ContainsAny(text, "bracket", "mount"))
        {
            return "bracket";
        }

        return "";
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static double P(Dictionary<string, double>? parameters, string key, double fallback)
    {
        if (parameters == null)
        {
            return fallback;
        }

        return parameters.TryGetValue(key, out var value) ? value : fallback;
    }

    private static EngineeringValidationSummary EvaluateToolValidation(
        CadGenerateResponse cadResult,
        string? partType,
        Dictionary<string, double>? parameters,
        bool strictValidation)
    {
        var summary = new EngineeringValidationSummary();

        if (!cadResult.Success)
        {
            summary.Errors.Add(cadResult.Error ?? "CAD generation failed.");
            summary.Passed = false;
            return summary;
        }

        var validation = cadResult.Validation;
        if (validation == null)
        {
            summary.Errors.Add("CAD response did not include validation payload.");
            summary.Passed = false;
            return summary;
        }

        if (validation.Warnings.Count > 0)
        {
            summary.Warnings.AddRange(validation.Warnings);
        }

        if (!strictValidation)
        {
            summary.Passed = true;
            return summary;
        }

        if (!validation.IsWatertight)
        {
            summary.Errors.Add("Mesh is not watertight.");
        }

        var bb = validation.BoundingBoxMm ?? new Dictionary<string, float>();
        var x = bb.GetValueOrDefault("x", 0f);
        var y = bb.GetValueOrDefault("y", 0f);
        var z = bb.GetValueOrDefault("z", 0f);

        if (x <= 0 || y <= 0 || z <= 0)
        {
            summary.Errors.Add("Bounding box is missing or has non-positive dimensions.");
        }

        var minTriangles = GetMinimumTriangleCount(partType, parameters);
        if (validation.TriangleCount < minTriangles)
        {
            summary.Errors.Add(
                $"Triangle count {validation.TriangleCount} is below the minimum expected {minTriangles} for part type '{partType ?? "unknown"}'.");
        }

        var expectedLength = ParameterOrNull(parameters, "length_mm");
        var expectedWidth = ParameterOrNull(parameters, "width_mm");
        var expectedHeight = ParameterOrNull(parameters, "height_mm");
        CheckDimension(summary, "length", expectedLength, x);
        CheckDimension(summary, "width", expectedWidth, y);
        CheckDimension(summary, "height", expectedHeight, z);

        var type = NormalizePartType(partType);
        var radialMajor = Math.Max(x, y);
        var radialMinor = Math.Min(x, y);

        if (type is "shaft" or "pin")
        {
            var expectedDiameter = ParameterOrNull(parameters, "diameter_mm");
            CheckDimension(summary, "diameter", expectedDiameter, radialMajor, toleranceRatio: 0.2);

            if (radialMinor > 0.01)
            {
                var ratio = radialMajor / radialMinor;
                if (ratio > 1.2)
                {
                    summary.Warnings.Add(
                        $"Cross-section ratio is {ratio.ToString("0.###", CultureInfo.InvariantCulture)}; expected near-cylindrical section.");
                }
            }
        }

        if (type == "bearing")
        {
            CheckDimension(summary, "outer_diameter", ParameterOrNull(parameters, "outer_diameter_mm"), radialMajor, toleranceRatio: 0.2);
            CheckDimension(summary, "width", ParameterOrNull(parameters, "width_mm"), z, toleranceRatio: 0.2);
        }

        if (type == "gear")
        {
            var module = ParameterOrNull(parameters, "module");
            var teeth = ParameterOrNull(parameters, "teeth");
            if (module.HasValue && teeth.HasValue)
            {
                var expectedOuter = module.Value * (teeth.Value + 2.0);
                CheckDimension(summary, "gear_outer_diameter", expectedOuter, radialMajor, toleranceRatio: 0.35);
            }
        }

        summary.Passed = summary.Errors.Count == 0;
        return summary;
    }

    private static string? AppendToolValidationContext(
        string? feedbackPrompt,
        string originalRequest,
        EngineeringValidationSummary validationSummary)
    {
        if (validationSummary.Passed || validationSummary.Errors.Count == 0)
        {
            return feedbackPrompt;
        }

        var failures = string.Join(
            "\n",
            validationSummary.Errors.Select(e => $"- {e}"));
        var warnings = validationSummary.Warnings.Count == 0
            ? ""
            : "\nTool warnings:\n" + string.Join("\n", validationSummary.Warnings.Select(w => $"- {w}"));

        var extra = $@"
Additional deterministic engineering checks FAILED:
{failures}{warnings}

Revise the CadQuery script to satisfy these checks while preserving already-correct geometry.
Output ONLY the corrected Python script (assign final solid to `result`).";

        if (string.IsNullOrWhiteSpace(feedbackPrompt))
        {
            return $@"Original request: {originalRequest}
{extra}";
        }

        return feedbackPrompt + "\n\n" + extra.Trim();
    }

    private static string BuildValidationFailureMessage(
        CadGenerateResponse cadResult,
        EngineeringValidationSummary validationSummary)
    {
        if (validationSummary.Errors.Count == 0)
        {
            return cadResult.Error ?? "CAD generation failed with no feedback.";
        }

        var errors = string.Join("; ", validationSummary.Errors);
        var prefix = string.IsNullOrWhiteSpace(cadResult.Error)
            ? "Deterministic validation failed"
            : cadResult.Error;
        return $"{prefix}. {errors}";
    }

    private static int GetMinimumTriangleCount(string? partType, Dictionary<string, double>? parameters)
    {
        var normalized = NormalizePartType(partType);
        var baseline = normalized switch
        {
            "gear" => 120,
            "housing" => 90,
            "bearing" => 70,
            "shaft" => 36,
            "pin" => 24,
            "plate" => 18,
            "bracket" => 24,
            _ => 20
        };

        var teeth = ParameterOrNull(parameters, "teeth");
        if (normalized == "gear" && teeth.HasValue)
        {
            baseline = Math.Max(baseline, (int)Math.Round(teeth.Value * 5.0));
        }

        return baseline;
    }

    private static double? ParameterOrNull(Dictionary<string, double>? parameters, string key)
    {
        if (parameters == null)
        {
            return null;
        }

        return parameters.TryGetValue(key, out var value) ? value : null;
    }

    private static void CheckDimension(
        EngineeringValidationSummary summary,
        string label,
        double? expected,
        double actual,
        double toleranceRatio = 0.25,
        double minToleranceMm = 0.8)
    {
        if (!expected.HasValue || expected.Value <= 0 || actual <= 0)
        {
            return;
        }

        var tolerance = Math.Max(minToleranceMm, Math.Abs(expected.Value) * toleranceRatio);
        var error = Math.Abs(actual - expected.Value);
        if (error > tolerance)
        {
            summary.Errors.Add(
                $"{label} mismatch: expected about {expected.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm, got {actual.ToString("0.###", CultureInfo.InvariantCulture)} mm.");
        }
    }

    private static string BuildBoxScript(CadDimensionSpec? dimensions)
    {
        var l = Math.Max(8.0, dimensions?.LengthMm ?? 30f);
        var w = Math.Max(8.0, dimensions?.WidthMm ?? 20f);
        var h = Math.Max(4.0, dimensions?.HeightMm ?? 10f);

        return $@"import cadquery as cq
import math

length = {l.ToString("0.###", CultureInfo.InvariantCulture)}
width = {w.ToString("0.###", CultureInfo.InvariantCulture)}
height = {h.ToString("0.###", CultureInfo.InvariantCulture)}

result = cq.Workplane(""XY"").box(length, width, height)";
    }

    private static string BuildBracketScript(CadDimensionSpec? dimensions)
    {
        var length = Math.Max(40.0, dimensions?.LengthMm ?? 60f);
        var width = Math.Max(20.0, dimensions?.WidthMm ?? 30f);
        var height = Math.Max(20.0, dimensions?.HeightMm ?? 40f);
        var thickness = Math.Max(4.0, Math.Min(length, Math.Min(width, height)) * 0.12);
        var holeDia = Math.Max(4.0, thickness * 0.8);
        var offset = Math.Max(thickness * 1.8, holeDia * 1.4);

        return $@"import cadquery as cq
import math

length = {length.ToString("0.###", CultureInfo.InvariantCulture)}
width = {width.ToString("0.###", CultureInfo.InvariantCulture)}
height = {height.ToString("0.###", CultureInfo.InvariantCulture)}
thickness = {thickness.ToString("0.###", CultureInfo.InvariantCulture)}
hole_dia = {holeDia.ToString("0.###", CultureInfo.InvariantCulture)}
offset = {offset.ToString("0.###", CultureInfo.InvariantCulture)}

base = cq.Workplane(""XY"").box(length, width, thickness)
upright = cq.Workplane(""XY"").box(thickness, width, height).translate(
    (-(length / 2.0) + (thickness / 2.0), 0, (height / 2.0) - (thickness / 2.0))
)
part = base.union(upright)

part = part.faces("">Z"").workplane().pushPoints([
    (-(length / 2.0) + offset, 0),
    ((length / 2.0) - offset, 0)
]).hole(hole_dia)

result = part";
    }

    private static string BuildPlateScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var length = Math.Max(20.0, dimensions?.LengthMm ?? P(parameters, "length_mm", 100.0));
        var width = Math.Max(20.0, dimensions?.WidthMm ?? P(parameters, "width_mm", 60.0));
        var thickness = Math.Max(2.0, dimensions?.HeightMm ?? P(parameters, "thickness_mm", 6.0));
        var holeDia = Math.Max(2.0, P(parameters, "hole_diameter_mm", 6.0));
        var inset = Math.Max(holeDia * 1.3, P(parameters, "hole_inset_mm", 12.0));

        return $@"import cadquery as cq
import math

length = {length.ToString("0.###", CultureInfo.InvariantCulture)}
width = {width.ToString("0.###", CultureInfo.InvariantCulture)}
thickness = {thickness.ToString("0.###", CultureInfo.InvariantCulture)}
hole_dia = {holeDia.ToString("0.###", CultureInfo.InvariantCulture)}
inset = {inset.ToString("0.###", CultureInfo.InvariantCulture)}

result = cq.Workplane(""XY"").box(length, width, thickness)
result = result.faces("">Z"").workplane().pushPoints([
    (-(length/2.0)+inset, -(width/2.0)+inset),
    ((length/2.0)-inset, -(width/2.0)+inset),
    (-(length/2.0)+inset, (width/2.0)-inset),
    ((length/2.0)-inset, (width/2.0)-inset)
]).hole(hole_dia)";
    }

    private static string BuildPinScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var length = Math.Max(4.0, dimensions?.LengthMm ?? P(parameters, "length_mm", 20.0));
        var diameter = Math.Max(1.0, dimensions?.WidthMm ?? P(parameters, "diameter_mm", 3.0));
        var chamfer = Math.Min(diameter * 0.25, Math.Max(0.0, P(parameters, "chamfer_mm", 0.3)));

        return $@"import cadquery as cq
import math

length = {length.ToString("0.###", CultureInfo.InvariantCulture)}
diameter = {diameter.ToString("0.###", CultureInfo.InvariantCulture)}
chamfer = {chamfer.ToString("0.###", CultureInfo.InvariantCulture)}

result = cq.Workplane(""XY"").circle(diameter / 2.0).extrude(length)
if chamfer > 0.01:
    result = result.faces("">Z"").edges().chamfer(chamfer)
    result = result.faces(""<Z"").edges().chamfer(chamfer)";
    }

    private static string BuildShaftScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var length = Math.Max(10.0, dimensions?.LengthMm ?? P(parameters, "length_mm", 120.0));
        var diameter = Math.Max(2.0, dimensions?.WidthMm ?? P(parameters, "diameter_mm", 12.0));
        var shoulderDiameter = Math.Max(diameter, P(parameters, "shoulder_diameter_mm", diameter * 1.35));
        var shoulderLength = Math.Max(0.0, P(parameters, "shoulder_length_mm", Math.Min(length * 0.25, 24.0)));

        return $@"import cadquery as cq
import math

length = {length.ToString("0.###", CultureInfo.InvariantCulture)}
diameter = {diameter.ToString("0.###", CultureInfo.InvariantCulture)}
shoulder_diameter = {shoulderDiameter.ToString("0.###", CultureInfo.InvariantCulture)}
shoulder_length = {shoulderLength.ToString("0.###", CultureInfo.InvariantCulture)}

shaft = cq.Workplane(""XY"").circle(diameter / 2.0).extrude(length)
if shoulder_length > 0.01:
    shoulder = cq.Workplane(""XY"").circle(shoulder_diameter / 2.0).extrude(shoulder_length)
    shoulder = shoulder.translate((0, 0, (length - shoulder_length) / 2.0))
    shaft = shaft.union(shoulder)

result = shaft";
    }

    private static string BuildBearingScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var outer = Math.Max(4.0, dimensions?.LengthMm ?? P(parameters, "outer_diameter_mm", 22.0));
        var width = Math.Max(2.0, dimensions?.HeightMm ?? P(parameters, "width_mm", 7.0));
        var inner = Math.Max(1.0, P(parameters, "inner_diameter_mm", Math.Max(outer * 0.45, 4.0)));
        if (inner >= outer - 1.0)
        {
            inner = outer - 1.0;
        }

        return $@"import cadquery as cq
import math

outer_d = {outer.ToString("0.###", CultureInfo.InvariantCulture)}
inner_d = {inner.ToString("0.###", CultureInfo.InvariantCulture)}
width = {width.ToString("0.###", CultureInfo.InvariantCulture)}

ring = cq.Workplane(""XY"").circle(outer_d / 2.0).extrude(width)
result = ring.faces("">Z"").workplane().hole(inner_d)";
    }

    private static string BuildHousingScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var length = Math.Max(20.0, dimensions?.LengthMm ?? P(parameters, "length_mm", 80.0));
        var width = Math.Max(20.0, dimensions?.WidthMm ?? P(parameters, "width_mm", 50.0));
        var height = Math.Max(20.0, dimensions?.HeightMm ?? P(parameters, "height_mm", 55.0));
        var wall = Math.Max(2.0, P(parameters, "wall_mm", 4.0));
        var bore = Math.Max(4.0, P(parameters, "center_bore_mm", Math.Min(length, width) * 0.35));

        return $@"import cadquery as cq
import math

length = {length.ToString("0.###", CultureInfo.InvariantCulture)}
width = {width.ToString("0.###", CultureInfo.InvariantCulture)}
height = {height.ToString("0.###", CultureInfo.InvariantCulture)}
wall = {wall.ToString("0.###", CultureInfo.InvariantCulture)}
center_bore = {bore.ToString("0.###", CultureInfo.InvariantCulture)}

outer = cq.Workplane(""XY"").box(length, width, height)
inner = cq.Workplane(""XY"").box(max(length - 2.0 * wall, 2.0), max(width - 2.0 * wall, 2.0), max(height - wall, 2.0))
inner = inner.translate((0, 0, wall / 2.0))
part = outer.cut(inner)
result = part.faces("">Z"").workplane().hole(center_bore)";
    }

    private static string BuildGearScript(CadDimensionSpec? dimensions, Dictionary<string, double>? parameters)
    {
        var teeth = Math.Max(8, (int)Math.Round(P(parameters, "teeth", 20)));
        var module = Math.Max(0.6, P(parameters, "module", 2.0));
        var faceWidth = Math.Max(2.0, dimensions?.HeightMm ?? P(parameters, "face_width_mm", 10.0));
        var bore = Math.Max(1.0, P(parameters, "bore_diameter_mm", 8.0));
        var pressure = Math.Clamp(P(parameters, "pressure_angle_deg", 20.0), 14.5, 30.0);

        var pitch = teeth * module;
        var root = Math.Max(2.0, pitch - 2.5 * module);
        var outer = pitch + 2.0 * module;
        var toothHeight = Math.Max(0.8, (outer - root) / 2.0);
        var toothWidth = Math.Max(0.8, (Math.PI * pitch / teeth) * 0.45);

        return $@"import cadquery as cq
import math

teeth = {teeth}
face_width = {faceWidth.ToString("0.###", CultureInfo.InvariantCulture)}
bore_diameter = {bore.ToString("0.###", CultureInfo.InvariantCulture)}
pressure_angle_deg = {pressure.ToString("0.###", CultureInfo.InvariantCulture)}
root_d = {root.ToString("0.###", CultureInfo.InvariantCulture)}
outer_d = {outer.ToString("0.###", CultureInfo.InvariantCulture)}
tooth_h = {toothHeight.ToString("0.###", CultureInfo.InvariantCulture)}
tooth_w = {toothWidth.ToString("0.###", CultureInfo.InvariantCulture)}

root = cq.Workplane(""XY"").circle(root_d / 2.0).extrude(face_width)
tooth = cq.Workplane(""XY"").rect(tooth_w, tooth_h).extrude(face_width)
tooth = tooth.translate((root_d / 2.0 + tooth_h / 2.0, 0, 0))

gear = root
for i in range(teeth):
    gear = gear.union(tooth.rotate((0,0,0), (0,0,1), i * (360.0 / teeth)))

result = gear.faces("">Z"").workplane().hole(bore_diameter)";
    }
}
