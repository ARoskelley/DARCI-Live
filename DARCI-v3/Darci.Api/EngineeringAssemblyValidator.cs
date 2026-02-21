using System.Globalization;

namespace Darci.Api;

public sealed class EngineeringValidationIssue
{
    public string Severity { get; init; } = "warning";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? PartA { get; init; }
    public string? PartB { get; init; }
}

public sealed class EngineeringValidationReport
{
    public bool Passed { get; init; }
    public List<EngineeringValidationIssue> Issues { get; init; } = new();
}

public static class EngineeringAssemblyValidator
{
    public static EngineeringValidationReport Validate(
        IReadOnlyList<EngineeringCollectionPartArtifact> parts,
        IReadOnlyList<EngineeringAssemblyConnection> connections)
    {
        var issues = new List<EngineeringValidationIssue>();
        var byName = parts.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            if (!part.Success)
            {
                issues.Add(new EngineeringValidationIssue
                {
                    Severity = "error",
                    Code = "part_generation_failed",
                    Message = $"Part '{part.Name}' failed generation: {part.Error ?? "unknown error"}",
                    PartA = part.Name
                });
                continue;
            }

            ValidatePartGeometry(part, issues);
        }

        foreach (var c in connections)
        {
            if (!byName.TryGetValue(c.From, out var from))
            {
                issues.Add(MissingPart("from_part_missing", c.From, c.To));
                continue;
            }

            if (!byName.TryGetValue(c.To, out var to))
            {
                issues.Add(MissingPart("to_part_missing", c.To, c.From));
                continue;
            }

            ValidateConnection(from, to, c.Relation, issues);
        }

        return new EngineeringValidationReport
        {
            Passed = issues.All(i => i.Severity != "error"),
            Issues = issues
        };
    }

    private static EngineeringValidationIssue MissingPart(string code, string part, string other) => new()
    {
        Severity = "error",
        Code = code,
        Message = $"Referenced part '{part}' was not generated (relation with '{other}').",
        PartA = part,
        PartB = other
    };

    private static void ValidatePartGeometry(
        EngineeringCollectionPartArtifact part,
        List<EngineeringValidationIssue> issues)
    {
        var type = ResolvePartType(part);
        var bbox = part.BoundingBoxMm ?? new Dictionary<string, float>();
        var px = bbox.GetValueOrDefault("x", 0);
        var py = bbox.GetValueOrDefault("y", 0);
        var pz = bbox.GetValueOrDefault("z", 0);

        if (px <= 0 || py <= 0 || pz <= 0)
        {
            issues.Add(new EngineeringValidationIssue
            {
                Severity = "warning",
                Code = "bbox_missing",
                Message = $"Part '{part.Name}' is missing valid bounding box values.",
                PartA = part.Name
            });
            return;
        }

        if (IsCylindricalType(type))
        {
            var radialMajor = Math.Max(px, py);
            var radialMinor = Math.Min(px, py);
            if (radialMinor > 0.1)
            {
                var radialRatio = radialMajor / radialMinor;
                if (radialRatio > 1.3)
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "warning",
                        Code = "cylindrical_cross_section_mismatch",
                        Message = $"Part '{part.Name}' is typed as '{type}' but X/Y ratio is {Fmt(radialRatio)} (expected near round).",
                        PartA = part.Name
                    });
                }
            }
        }

        if (type == "gear" && part.TriangleCount.HasValue && part.TriangleCount.Value < 40)
        {
            issues.Add(new EngineeringValidationIssue
            {
                Severity = "warning",
                Code = "gear_triangle_count_low",
                Message = $"Gear '{part.Name}' has only {part.TriangleCount.Value} triangles; geometry may be overly simplified.",
                PartA = part.Name
            });
        }

        if (type == "gear" && IsDeterministicFallbackSource(part.GenerationSource))
        {
            issues.Add(new EngineeringValidationIssue
            {
                Severity = "error",
                Code = "gear_used_box_fallback_path",
                Message = $"Gear '{part.Name}' came from deterministic fallback path. Meshing accuracy is unreliable without provider-generated geometry.",
                PartA = part.Name
            });
        }

        if (type == "gear")
        {
            var teeth = P(part.Parameters, "teeth");
            var module = P(part.Parameters, "module");
            if (teeth.HasValue && module.HasValue)
            {
                var expectedOuter = module.Value * (teeth.Value + 2.0);
                var actualOuter = Math.Max(px, py);
                if (!Within(actualOuter, expectedOuter, 0.35))
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "warning",
                        Code = "gear_outer_diameter_mismatch",
                        Message = $"Gear '{part.Name}' outer diameter expected ~{Fmt(expectedOuter)} mm, got {Fmt(actualOuter)} mm.",
                        PartA = part.Name
                    });
                }
            }
        }

        if (type == "bearing")
        {
            var outer = P(part.Parameters, "outer_diameter_mm");
            var width = P(part.Parameters, "width_mm");
            if (outer.HasValue && !Within(Math.Max(px, py), outer.Value, 0.2))
            {
                issues.Add(new EngineeringValidationIssue
                {
                    Severity = "warning",
                    Code = "bearing_outer_mismatch",
                    Message = $"Bearing '{part.Name}' outer diameter mismatch: expected {Fmt(outer.Value)} mm, got {Fmt(Math.Max(px, py))} mm.",
                    PartA = part.Name
                });
            }

            if (width.HasValue && !Within(pz, width.Value, 0.2))
            {
                issues.Add(new EngineeringValidationIssue
                {
                    Severity = "warning",
                    Code = "bearing_width_mismatch",
                    Message = $"Bearing '{part.Name}' width mismatch: expected {Fmt(width.Value)} mm, got {Fmt(pz)} mm.",
                    PartA = part.Name
                });
            }
        }
    }

    private static void ValidateConnection(
        EngineeringCollectionPartArtifact from,
        EngineeringCollectionPartArtifact to,
        string relation,
        List<EngineeringValidationIssue> issues)
    {
        var rel = (relation ?? "").ToLowerInvariant();
        var fromType = ResolvePartType(from);
        var toType = ResolvePartType(to);

        if (rel.Contains("mates") || rel.Contains("mesh"))
        {
            if ((fromType == "shaft" && toType == "gear") || (fromType == "gear" && toType == "shaft"))
            {
                var shaft = fromType == "shaft" ? from : to;
                var gear = fromType == "gear" ? from : to;

                var shaftD = P(shaft.Parameters, "diameter_mm");
                var gearBore = P(gear.Parameters, "bore_diameter_mm");
                if (shaftD.HasValue && gearBore.HasValue)
                {
                    if (shaftD.Value > gearBore.Value + 0.05)
                    {
                        issues.Add(new EngineeringValidationIssue
                        {
                            Severity = "error",
                            Code = "shaft_gear_bore_interference",
                            Message = $"Shaft '{shaft.Name}' dia {Fmt(shaftD.Value)} mm exceeds gear '{gear.Name}' bore {Fmt(gearBore.Value)} mm.",
                            PartA = shaft.Name,
                            PartB = gear.Name
                        });
                    }
                }
            }

            if (fromType == "gear" && toType == "gear")
            {
                if (IsDeterministicFallbackSource(from.GenerationSource)
                    || IsDeterministicFallbackSource(to.GenerationSource))
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "error",
                        Code = "gear_mesh_low_fidelity_source",
                        Message = $"Gear mesh '{from.Name}' <-> '{to.Name}' uses fallback geometry; rerun with provider-backed CAD generation.",
                        PartA = from.Name,
                        PartB = to.Name
                    });
                }

                var moduleA = P(from.Parameters, "module");
                var moduleB = P(to.Parameters, "module");
                var teethA = P(from.Parameters, "teeth");
                var teethB = P(to.Parameters, "teeth");

                if (!moduleA.HasValue || !moduleB.HasValue || !teethA.HasValue || !teethB.HasValue)
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "error",
                        Code = "gear_mesh_parameters_missing",
                        Message = $"Gear mesh '{from.Name}' <-> '{to.Name}' requires module and teeth counts on both gears.",
                        PartA = from.Name,
                        PartB = to.Name
                    });
                }

                if (moduleA.HasValue && moduleB.HasValue && !Within(moduleA.Value, moduleB.Value, 0.05))
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "error",
                        Code = "gear_module_mismatch",
                        Message = $"Gears '{from.Name}' and '{to.Name}' have module mismatch ({Fmt(moduleA.Value)} vs {Fmt(moduleB.Value)} mm).",
                        PartA = from.Name,
                        PartB = to.Name
                    });
                }

                if (moduleA.HasValue && moduleB.HasValue && teethA.HasValue && teethB.HasValue &&
                    from.X.HasValue && from.Y.HasValue && to.X.HasValue && to.Y.HasValue)
                {
                    var averageModule = (moduleA.Value + moduleB.Value) / 2.0;
                    var expectedCenterDistance = averageModule * (teethA.Value + teethB.Value) / 2.0;
                    var actualCenterDistance = Distance2D(from.X.Value, from.Y.Value, to.X.Value, to.Y.Value);
                    if (!Within(actualCenterDistance, expectedCenterDistance, 0.2))
                    {
                        issues.Add(new EngineeringValidationIssue
                        {
                            Severity = "warning",
                            Code = "gear_mesh_center_distance_mismatch",
                            Message = $"Gears '{from.Name}' and '{to.Name}' center distance is {Fmt(actualCenterDistance)} mm; expected about {Fmt(expectedCenterDistance)} mm.",
                            PartA = from.Name,
                            PartB = to.Name
                        });
                    }
                }
            }
        }

        if (rel.Contains("houses"))
        {
            if ((fromType == "housing" && toType == "bearing") || (fromType == "bearing" && toType == "housing"))
            {
                var housing = fromType == "housing" ? from : to;
                var bearing = fromType == "bearing" ? from : to;
                var bore = P(housing.Parameters, "center_bore_mm");
                var od = P(bearing.Parameters, "outer_diameter_mm");
                if (bore.HasValue && od.HasValue && bore.Value + 0.1 < od.Value)
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "error",
                        Code = "housing_bearing_fit_error",
                        Message = $"Housing '{housing.Name}' bore {Fmt(bore.Value)} mm is smaller than bearing '{bearing.Name}' OD {Fmt(od.Value)} mm.",
                        PartA = housing.Name,
                        PartB = bearing.Name
                    });
                }
            }
        }

        if (rel.Contains("retained"))
        {
            if ((fromType == "shaft" && toType == "pin") || (fromType == "pin" && toType == "shaft"))
            {
                var shaft = fromType == "shaft" ? from : to;
                var pin = fromType == "pin" ? from : to;
                var shaftD = P(shaft.Parameters, "diameter_mm");
                var pinD = P(pin.Parameters, "diameter_mm");
                if (shaftD.HasValue && pinD.HasValue && pinD.Value >= shaftD.Value)
                {
                    issues.Add(new EngineeringValidationIssue
                    {
                        Severity = "error",
                        Code = "pin_too_large_for_shaft",
                        Message = $"Pin '{pin.Name}' dia {Fmt(pinD.Value)} mm must be smaller than shaft '{shaft.Name}' dia {Fmt(shaftD.Value)} mm.",
                        PartA = pin.Name,
                        PartB = shaft.Name
                    });
                }
            }
        }
    }

    private static double? P(Dictionary<string, double>? parameters, string key)
    {
        if (parameters == null) return null;
        return parameters.TryGetValue(key, out var value) ? value : null;
    }

    private static string ResolvePartType(EngineeringCollectionPartArtifact part)
    {
        var explicitType = NormalizeType(part.PartType);
        if (!string.IsNullOrWhiteSpace(explicitType))
        {
            return explicitType;
        }

        var text = $"{part.Name} {part.Description}".ToLowerInvariant();
        if (ContainsAny(text, "housing", "axle box", "axlebox", "gearbox", "enclosure", "case")) return "housing";
        if (ContainsAny(text, "bearing", "bushing")) return "bearing";
        if (ContainsAny(text, "gear", "sprocket", "toothed")) return "gear";
        if (ContainsAny(text, "shaft", "axle", "driveshaft", "drive shaft", "rod", "spindle")) return "shaft";
        if (ContainsAny(text, "pin", "dowel", "retaining pin")) return "pin";
        if (ContainsAny(text, "plate", "flange", "panel")) return "plate";
        if (ContainsAny(text, "bracket", "mount")) return "bracket";
        return "";
    }

    private static bool IsDeterministicFallbackSource(string? generationSource)
    {
        if (string.IsNullOrWhiteSpace(generationSource))
        {
            return false;
        }

        return generationSource.Contains("deterministic:fallback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCylindricalType(string type)
    {
        return type == "gear" || type == "bearing" || type == "shaft" || type == "pin";
    }

    private static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "";
        }

        var normalized = type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "axle" => "shaft",
            "axle-shaft" => "shaft",
            "driveshaft" => "shaft",
            "dowel" => "pin",
            "bushing" => "bearing",
            "gearbox" => "housing",
            "axlebox" => "housing",
            "enclosure" => "housing",
            "case" => "housing",
            _ => normalized
        };
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

    private static double Distance2D(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool Within(double actual, double expected, double toleranceRatio)
    {
        if (expected == 0) return Math.Abs(actual) < 1e-6;
        return Math.Abs(actual - expected) <= Math.Abs(expected) * toleranceRatio;
    }

    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
