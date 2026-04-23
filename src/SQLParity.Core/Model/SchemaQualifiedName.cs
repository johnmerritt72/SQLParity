using System;

namespace SQLParity.Core.Model;

/// <summary>
/// Stable identity key for a database object. Used for matching objects
/// across two databases during comparison. Case-insensitive, since SQL
/// Server default collation is case-insensitive.
/// </summary>
public sealed class SchemaQualifiedName : IEquatable<SchemaQualifiedName>
{
    public string Schema { get; }
    public string? Parent { get; }
    public string Name { get; }

    private SchemaQualifiedName(string schema, string? parent, string name)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Parent = parent;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Identity for top-level objects: tables, views, procs, etc.
    /// </summary>
    public static SchemaQualifiedName TopLevel(string schema, string name)
        => new(schema, null, name);

    /// <summary>
    /// Identity for child objects: columns, indexes, constraints, triggers.
    /// </summary>
    public static SchemaQualifiedName Child(string schema, string parent, string name)
        => new(schema, parent ?? throw new ArgumentNullException(nameof(parent)), name);

    public bool Equals(SchemaQualifiedName? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Parent, other.Parent, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as SchemaQualifiedName);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Schema, StringComparer.OrdinalIgnoreCase);
        if (Parent is not null)
            hash.Add(Parent, StringComparer.OrdinalIgnoreCase);
        else
            hash.Add(0);
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    public override string ToString()
        => Parent is null
            ? $"[{Schema}].[{Name}]"
            : $"[{Schema}].[{Parent}].[{Name}]";
}
