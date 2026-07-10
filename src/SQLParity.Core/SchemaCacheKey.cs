namespace SQLParity.Core;

/// <summary>
/// Builds the string keys used by the VSIX schema cache. A full-database
/// read is keyed "server|database"; a schema-scoped read is keyed
/// "server|database|schema" so a partial read can never be served (or
/// overwritten) as the whole database. Consumers store keys in a
/// case-insensitive dictionary, so no casing normalization happens here.
/// </summary>
public static class SchemaCacheKey
{
    public static string Make(string? server, string? database, string? schemaFilter = null)
    {
        var key = (server ?? "").Trim() + "|" + (database ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(schemaFilter))
            key += "|" + schemaFilter!.Trim();
        return key;
    }
}
