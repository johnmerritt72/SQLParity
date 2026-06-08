using System;
using System.Collections.Generic;

namespace SQLParity.Core.Model;

/// <summary>The securable class a permission applies to.</summary>
public enum PermissionClass
{
    /// <summary>An object-level grant (table, view, proc, function, sequence, type).</summary>
    Object,
    /// <summary>A schema-level grant (e.g. GRANT EXECUTE ON SCHEMA::dbo).</summary>
    Schema
}

/// <summary>The state of a permission as stored in sys.database_permissions.</summary>
public enum PermissionState
{
    /// <summary>A plain GRANT.</summary>
    Grant,
    /// <summary>A GRANT WITH GRANT OPTION (grantee may re-grant to others).</summary>
    GrantWithGrant,
    /// <summary>A DENY (explicitly blocks access, overrides grants).</summary>
    Deny
}

/// <summary>
/// One granted permission read from the catalog (immutable). For
/// <see cref="PermissionClass.Object"/> the target is the object's schema + name;
/// for <see cref="PermissionClass.Schema"/> both TargetSchema and TargetName are
/// the schema's name (so a single (Class, TargetSchema, TargetName) key works for
/// both classes).
/// </summary>
public sealed class PermissionModel
{
    public required PermissionClass Class { get; init; }
    public required string GranteeName { get; init; }      // principal (user/role) — cross-DB match key
    public required string PermissionName { get; init; }   // EXECUTE, SELECT, INSERT, REFERENCES, ...
    public required PermissionState State { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetName { get; init; }
}

/// <summary>
/// One detected permission difference — sibling of <see cref="ColumnChange"/>.
/// Identity is (GranteeName, PermissionName); State is the comparable value.
/// A null state means the permission is absent on that side. The parent
/// Change's ObjectType determines OBJECT vs SCHEMA class at script time.
/// </summary>
public sealed class PermissionChange
{
    public required string GranteeName { get; init; }
    public required string PermissionName { get; init; }

    /// <summary>State on Side A (the source). Null when the permission is absent on A.</summary>
    public PermissionState? StateSideA { get; init; }

    /// <summary>State on Side B (the destination). Null when the permission is absent on B.</summary>
    public PermissionState? StateSideB { get; init; }

    public RiskTier Risk { get; set; }
    public IReadOnlyList<RiskReason> Reasons { get; set; } = Array.Empty<RiskReason>();
}
