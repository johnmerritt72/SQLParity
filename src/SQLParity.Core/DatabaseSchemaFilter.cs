using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;

namespace SQLParity.Core;

/// <summary>
/// Filters an already-read <see cref="DatabaseSchema"/> down to a single
/// schema. Used when a schema-filtered compare can be satisfied from a
/// cached full-database read, for folder-mode Side B, and as the final
/// consistency pass at the end of <see cref="SchemaReader.ReadSchema"/>.
/// </summary>
public static class DatabaseSchemaFilter
{
    public static DatabaseSchema FilterToSchema(DatabaseSchema source, string? schemaName)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(schemaName))
            return source;

        var name = schemaName!.Trim();
        bool Matches(string s) => string.Equals(s, name, StringComparison.OrdinalIgnoreCase);

        var externalRefs = new Dictionary<(string Schema, string Name), IReadOnlyList<string>>();
        foreach (var kvp in source.ExternalReferences)
            if (Matches(kvp.Key.Schema))
                externalRefs[kvp.Key] = kvp.Value;

        return new DatabaseSchema
        {
            ServerName = source.ServerName,
            DatabaseName = source.DatabaseName,
            ReadAtUtc = source.ReadAtUtc,
            Schemas = source.Schemas.Where(s => Matches(s.Name)).ToList(),
            Tables = source.Tables.Where(t => Matches(t.Schema)).ToList(),
            Views = source.Views.Where(v => Matches(v.Schema)).ToList(),
            StoredProcedures = source.StoredProcedures.Where(p => Matches(p.Schema)).ToList(),
            Functions = source.Functions.Where(f => Matches(f.Schema)).ToList(),
            Sequences = source.Sequences.Where(s => Matches(s.Schema)).ToList(),
            Synonyms = source.Synonyms.Where(s => Matches(s.Schema)).ToList(),
            UserDefinedDataTypes = source.UserDefinedDataTypes.Where(t => Matches(t.Schema)).ToList(),
            UserDefinedTableTypes = source.UserDefinedTableTypes.Where(t => Matches(t.Schema)).ToList(),
            Permissions = FilterPermissions(source.Permissions, name),
            ExternalReferences = externalRefs,
        };
    }

    /// <summary>
    /// Keeps permissions whose target schema matches: object-level grants on
    /// objects in the schema, and the schema-level grants on the schema itself
    /// (both carry the schema in <see cref="PermissionModel.TargetSchema"/>).
    /// </summary>
    public static IReadOnlyList<PermissionModel> FilterPermissions(
        IReadOnlyList<PermissionModel> permissions, string? schemaName)
    {
        if (permissions == null || permissions.Count == 0 || string.IsNullOrWhiteSpace(schemaName))
            return permissions ?? Array.Empty<PermissionModel>();

        var name = schemaName!.Trim();
        return permissions
            .Where(p => string.Equals(p.TargetSchema, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
