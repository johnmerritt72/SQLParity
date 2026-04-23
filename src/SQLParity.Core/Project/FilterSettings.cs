using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// Filter configuration for a comparison. Controls which objects are
/// included in the comparison results.
/// </summary>
public sealed class FilterSettings
{
    /// <summary>Object types to include. If empty, all types are included.</summary>
    public IReadOnlyList<ObjectType> IncludedObjectTypes { get; init; } = Array.Empty<ObjectType>();

    /// <summary>Schema names to include. If empty, all schemas are included. Case-insensitive.</summary>
    public IReadOnlyList<string> IncludedSchemas { get; init; } = Array.Empty<string>();

    /// <summary>Schema names to exclude. Takes precedence over IncludedSchemas. Case-insensitive.</summary>
    public IReadOnlyList<string> ExcludedSchemas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Wildcard patterns for object names to exclude (e.g., "tmp_*", "*_bak").
    /// Uses * for any sequence, ? for one character. Case-insensitive.
    /// </summary>
    public IReadOnlyList<string> ExcludedNamePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Returns true if the given object should be included based on these filters.</summary>
    public bool ShouldInclude(ObjectType type, string schema, string name)
    {
        if (IncludedObjectTypes.Count > 0 && !IncludedObjectTypes.Contains(type))
            return false;

        if (ExcludedSchemas.Count > 0 &&
            ExcludedSchemas.Any(s => string.Equals(s, schema, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (IncludedSchemas.Count > 0 &&
            !IncludedSchemas.Any(s => string.Equals(s, schema, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (ExcludedNamePatterns.Count > 0 &&
            ExcludedNamePatterns.Any(p => WildcardMatch(name, p)))
            return false;

        return true;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
