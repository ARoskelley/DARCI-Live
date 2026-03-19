#nullable enable

using Darci.Memory.Graph.Models;

namespace Darci.Memory.Graph;

public static class KnowledgeGraphExtensions
{
    public static bool MatchesName(this KgEntity entity, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return entity.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)
            || entity.Aliases.Any(alias => alias.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    public static string ToRelationText(this KgRelation relation, KgEntity from, KgEntity to)
        => $"{from.Name} ({from.EntityType}) - {relation.RelationType} -> {to.Name} ({to.EntityType})";
}
