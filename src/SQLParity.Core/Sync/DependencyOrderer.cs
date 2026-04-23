using System;
using System.Collections.Generic;
using System.Linq;
using SQLParity.Core.Model;

namespace SQLParity.Core.Sync;

public static class DependencyOrderer
{
    // Functions before procs, since procs may call functions
    private static readonly Dictionary<ObjectType, int> CreateOrder = new()
    {
        { ObjectType.Schema, 0 },
        { ObjectType.UserDefinedDataType, 1 },
        { ObjectType.UserDefinedTableType, 2 },
        { ObjectType.Table, 3 },
        { ObjectType.Index, 4 },
        { ObjectType.ForeignKey, 5 },
        { ObjectType.CheckConstraint, 6 },
        { ObjectType.Trigger, 7 },
        { ObjectType.Sequence, 8 },
        { ObjectType.Synonym, 9 },
        { ObjectType.View, 10 },
        { ObjectType.UserDefinedFunction, 11 },
        { ObjectType.StoredProcedure, 12 },
    };

    private static readonly Dictionary<ObjectType, int> DropOrder = new()
    {
        { ObjectType.StoredProcedure, 0 },
        { ObjectType.UserDefinedFunction, 1 },
        { ObjectType.View, 2 },
        { ObjectType.Synonym, 3 },
        { ObjectType.Sequence, 4 },
        { ObjectType.Trigger, 5 },
        { ObjectType.CheckConstraint, 6 },
        { ObjectType.ForeignKey, 7 },
        { ObjectType.Index, 8 },
        { ObjectType.Table, 9 },
        { ObjectType.UserDefinedTableType, 10 },
        { ObjectType.UserDefinedDataType, 11 },
        { ObjectType.Schema, 12 },
    };

    private static readonly HashSet<ObjectType> RoutineTypes = new()
    {
        ObjectType.StoredProcedure,
        ObjectType.UserDefinedFunction,
        ObjectType.View,
    };

    public static IEnumerable<Change> Order(IEnumerable<Change> changes)
    {
        var list = changes.ToList();
        if (list.Count == 0) return list;

        var drops = list.Where(c => c.Status == ChangeStatus.Dropped)
            .OrderBy(c => GetDropOrder(c.ObjectType))
            .ThenBy(c => c.Id.ToString());

        // For creates and modifies, split routines from non-routines.
        // Non-routines use simple type ordering.
        // Routines get dependency-aware ordering: new first, then modified,
        // with basic dependency sorting within each group.
        var modifies = list.Where(c => c.Status == ChangeStatus.Modified).ToList();
        var creates = list.Where(c => c.Status == ChangeStatus.New).ToList();

        var orderedModifies = OrderWithRoutineDependencies(modifies);
        var orderedCreates = OrderWithRoutineDependencies(creates);

        return drops.Concat(orderedModifies).Concat(orderedCreates);
    }

    /// <summary>
    /// Orders a set of changes with dependency awareness for routines.
    /// Non-routines go first (by type order). Then routines are sorted so that
    /// routines referenced by other routines go first.
    /// </summary>
    private static IEnumerable<Change> OrderWithRoutineDependencies(List<Change> changes)
    {
        // Non-routines: simple type order
        var nonRoutines = changes.Where(c => !RoutineTypes.Contains(c.ObjectType))
            .OrderBy(c => GetCreateOrder(c.ObjectType))
            .ThenBy(c => c.Id.ToString());

        // Routines: dependency-aware order
        var routines = changes.Where(c => RoutineTypes.Contains(c.ObjectType)).ToList();

        if (routines.Count == 0)
            return nonRoutines;

        var orderedRoutines = SortRoutinesByDependency(routines);

        return nonRoutines.Concat(orderedRoutines);
    }

    /// <summary>
    /// Sorts routines so that routines referenced by other routines go first.
    /// Uses a simple text-search heuristic: if routine A's DDL contains routine B's name,
    /// then A depends on B, and B should be scripted first.
    /// </summary>
    private static List<Change> SortRoutinesByDependency(List<Change> routines)
    {
        if (routines.Count <= 1)
            return routines;

        // Build a set of all routine names in this batch (schema.name)
        var nameToChange = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routines)
        {
            var fullName = r.Id.Schema + "." + r.Id.Name;
            nameToChange[fullName] = r;
        }

        // Build dependency graph: for each routine, find which other routines it references
        var dependsOn = new Dictionary<Change, HashSet<Change>>();
        foreach (var r in routines)
        {
            dependsOn[r] = new HashSet<Change>();
            var ddl = (r.DdlSideA ?? string.Empty).ToUpperInvariant();

            foreach (var kvp in nameToChange)
            {
                if (kvp.Value == r) continue; // Don't depend on self

                // Check if this routine's DDL references the other routine's name
                // Look for [schema].[name] or schema.name patterns
                var schemaName = kvp.Key.ToUpperInvariant();
                var parts = kvp.Key.Split('.');
                var bracketedName = "[" + parts[0].ToUpperInvariant() + "].[" + parts[1].ToUpperInvariant() + "]";
                var justName = parts[1].ToUpperInvariant();

                if (ddl.Contains(bracketedName) || ddl.Contains(schemaName)
                    || ddl.Contains("[" + justName + "]"))
                {
                    dependsOn[r].Add(kvp.Value);
                }
            }
        }

        // Topological sort (Kahn's algorithm)
        var inDegree = new Dictionary<Change, int>();
        foreach (var r in routines)
            inDegree[r] = 0;
        foreach (var kvp in dependsOn)
            foreach (var dep in kvp.Value)
                if (inDegree.ContainsKey(dep))
                    inDegree[kvp.Key]++;

        var queue = new Queue<Change>();
        foreach (var kvp in inDegree)
            if (kvp.Value == 0)
                queue.Enqueue(kvp.Key);

        var sorted = new List<Change>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            // For each routine that depends on current, decrement in-degree
            foreach (var kvp in dependsOn)
            {
                if (kvp.Value.Contains(current))
                {
                    inDegree[kvp.Key]--;
                    if (inDegree[kvp.Key] == 0)
                        queue.Enqueue(kvp.Key);
                }
            }
        }

        // Any routines not in sorted list have circular dependencies.
        // add them at the end (they'll need manual intervention)
        foreach (var r in routines)
        {
            if (!sorted.Contains(r))
                sorted.Add(r);
        }

        return sorted;
    }

    /// <summary>
    /// Identifies routines with circular dependencies in the given set.
    /// These need the stub/fill two-pass approach for safe scripting.
    /// </summary>
    public static List<Change> FindCircularDependencies(List<Change> routines)
    {
        if (routines.Count <= 1)
            return new List<Change>();

        var nameToChange = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routines)
        {
            var fullName = r.Id.Schema + "." + r.Id.Name;
            nameToChange[fullName] = r;
        }

        // Build dependency graph
        var dependsOn = new Dictionary<Change, HashSet<Change>>();
        foreach (var r in routines)
        {
            dependsOn[r] = new HashSet<Change>();
            var ddl = (r.DdlSideA ?? string.Empty).ToUpperInvariant();

            foreach (var kvp in nameToChange)
            {
                if (kvp.Value == r) continue;
                var parts = kvp.Key.Split('.');
                var bracketedName = "[" + parts[0].ToUpperInvariant() + "].[" + parts[1].ToUpperInvariant() + "]";
                var schemaName = kvp.Key.ToUpperInvariant();
                var justName = parts[1].ToUpperInvariant();

                if (ddl.Contains(bracketedName) || ddl.Contains(schemaName)
                    || ddl.Contains("[" + justName + "]"))
                {
                    dependsOn[r].Add(kvp.Value);
                }
            }
        }

        // Run topological sort to find what CAN'T be sorted (circular)
        var inDegree = new Dictionary<Change, int>();
        foreach (var r in routines)
            inDegree[r] = 0;
        foreach (var kvp in dependsOn)
            foreach (var dep in kvp.Value)
                if (inDegree.ContainsKey(dep))
                    inDegree[kvp.Key]++;

        var queue = new Queue<Change>();
        foreach (var kvp in inDegree)
            if (kvp.Value == 0)
                queue.Enqueue(kvp.Key);

        var sorted = new HashSet<Change>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);
            foreach (var kvp in dependsOn)
            {
                if (kvp.Value.Contains(current))
                {
                    inDegree[kvp.Key]--;
                    if (inDegree[kvp.Key] == 0)
                        queue.Enqueue(kvp.Key);
                }
            }
        }

        // Anything not in sorted has circular dependencies
        return routines.Where(r => !sorted.Contains(r)).ToList();
    }

    private static int GetCreateOrder(ObjectType type)
        => CreateOrder.TryGetValue(type, out var order) ? order : 99;

    private static int GetDropOrder(ObjectType type)
        => DropOrder.TryGetValue(type, out var order) ? order : 99;
}
