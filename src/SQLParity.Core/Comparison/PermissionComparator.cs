using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Core.Comparison;

/// <summary>
/// Identity of a permission target: securable class + schema + name.
/// Case-insensitive. For schema-class grants, Schema == Name == the schema name.
/// </summary>
public readonly struct PermissionTargetKey : IEquatable<PermissionTargetKey>
{
    public PermissionClass Class { get; }
    public string Schema { get; }
    public string Name { get; }

    public PermissionTargetKey(PermissionClass cls, string schema, string name)
    {
        Class = cls;
        Schema = schema ?? string.Empty;
        Name = name ?? string.Empty;
    }

    public bool Equals(PermissionTargetKey other) =>
        Class == other.Class
        && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is PermissionTargetKey k && Equals(k);

    public override int GetHashCode() => HashCode.Combine(
        (int)Class,
        StringComparer.OrdinalIgnoreCase.GetHashCode(Schema),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name));
}

/// <summary>
/// Set-diffs two databases' permission lists, grouped by target object/schema.
/// </summary>
public static class PermissionComparator
{
    public static IReadOnlyDictionary<PermissionTargetKey, IList<PermissionChange>> Compare(
        IReadOnlyList<PermissionModel> permsA,
        IReadOnlyList<PermissionModel> permsB)
    {
        // Index both sides by (target key) -> ((grantee, perm) -> state).
        var byTargetA = IndexByTarget(permsA);
        var byTargetB = IndexByTarget(permsB);

        var result = new Dictionary<PermissionTargetKey, IList<PermissionChange>>();

        // Union of all target keys.
        var allTargets = new HashSet<PermissionTargetKey>(byTargetA.Keys);
        foreach (var k in byTargetB.Keys) allTargets.Add(k);

        foreach (var target in allTargets)
        {
            byTargetA.TryGetValue(target, out var aMap);
            byTargetB.TryGetValue(target, out var bMap);
            aMap ??= EmptyGranteePermMap;
            bMap ??= EmptyGranteePermMap;

            // Union of (grantee, perm) keys for this target.
            var keys = new HashSet<GranteePermKey>(aMap.Keys);
            foreach (var k in bMap.Keys) keys.Add(k);

            var changes = new List<PermissionChange>();
            foreach (var k in keys)
            {
                bool inA = aMap.TryGetValue(k, out var stateA);
                bool inB = bMap.TryGetValue(k, out var stateB);

                if (inA && inB && stateA == stateB)
                    continue; // identical — no change

                changes.Add(new PermissionChange
                {
                    GranteeName = k.Grantee,
                    PermissionName = k.Permission,
                    StateSideA = inA ? stateA : (PermissionState?)null,
                    StateSideB = inB ? stateB : (PermissionState?)null,
                });
            }

            if (changes.Count > 0)
                result[target] = changes;
        }

        return result;
    }

    private static readonly Dictionary<GranteePermKey, PermissionState> EmptyGranteePermMap = new();

    private static Dictionary<PermissionTargetKey, Dictionary<GranteePermKey, PermissionState>> IndexByTarget(
        IReadOnlyList<PermissionModel> perms)
    {
        var byTarget = new Dictionary<PermissionTargetKey, Dictionary<GranteePermKey, PermissionState>>();
        foreach (var p in perms)
        {
            var target = new PermissionTargetKey(p.Class, p.TargetSchema, p.TargetName);
            if (!byTarget.TryGetValue(target, out var map))
            {
                map = new Dictionary<GranteePermKey, PermissionState>();
                byTarget[target] = map;
            }
            // Last write wins; duplicates shouldn't occur for a (grantee, perm, target).
            map[new GranteePermKey(p.GranteeName, p.PermissionName)] = p.State;
        }
        return byTarget;
    }

    private readonly struct GranteePermKey : IEquatable<GranteePermKey>
    {
        public string Grantee { get; }
        public string Permission { get; }

        public GranteePermKey(string grantee, string permission)
        {
            Grantee = grantee ?? string.Empty;
            Permission = permission ?? string.Empty;
        }

        public bool Equals(GranteePermKey other) =>
            string.Equals(Grantee, other.Grantee, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Permission, other.Permission, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => obj is GranteePermKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Grantee),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Permission));
    }
}
