using Microsoft.Data.SqlClient;

namespace SQLParity.Core.Sync;

/// <summary>
/// Lightweight COUNT query used by the pre-Apply "destination changed?"
/// verify path. Counts the same set of objects the <see cref="SchemaReader"/>
/// enumerates so the two sides of the comparison agree. SMO's IsSystemObject
/// filter is per-type: tables use just <c>is_ms_shipped</c>; views and
/// procedures additionally exclude objects carrying the SSMS-installed
/// <c>microsoft_database_tools_support</c> extended property (the diagram
/// support views/procs). Without applying the SMO-matching filter the verify
/// reports phantom drift on every Generate-Script / Apply-Live click in any
/// destination where database diagrams have been installed.
/// </summary>
public static class DestinationCountQuery
{
    /// <summary>
    /// Reads live object counts from <paramref name="connectionString"/>.
    /// When <paramref name="schemaFilter"/> is supplied, each per-type count
    /// is scoped to that schema only — this must mirror whatever schema
    /// scope was used to snapshot the comparison's destination side,
    /// otherwise a filtered compare always sees "extra" objects that simply
    /// belong to schemas outside the filter.
    /// </summary>
    public static (int tables, int views, int procs) Read(string connectionString, string? schemaFilter = null)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        string tablesPredicate = schemaFilter == null ? string.Empty : " AND SCHEMA_NAME(t.schema_id) = @schemaFilter";
        string viewsPredicate = schemaFilter == null ? string.Empty : " AND SCHEMA_NAME(v.schema_id) = @schemaFilter";
        string procsPredicate = schemaFilter == null ? string.Empty : " AND SCHEMA_NAME(p.schema_id) = @schemaFilter";

        cmd.CommandText = $@"
WITH tools_marked AS (
    SELECT major_id
    FROM sys.extended_properties
    WHERE class = 1 AND name = N'microsoft_database_tools_support'
)
SELECT
    (SELECT COUNT(*)
        FROM sys.tables t
        WHERE t.is_ms_shipped = 0{tablesPredicate}),
    (SELECT COUNT(*)
        FROM sys.views v
        WHERE v.is_ms_shipped = 0
          AND NOT EXISTS (SELECT 1 FROM tools_marked WHERE tools_marked.major_id = v.object_id){viewsPredicate}),
    (SELECT COUNT(*)
        FROM sys.procedures p
        WHERE p.is_ms_shipped = 0
          AND NOT EXISTS (SELECT 1 FROM tools_marked WHERE tools_marked.major_id = p.object_id){procsPredicate});";

        if (schemaFilter != null)
            cmd.Parameters.Add(new SqlParameter("@schemaFilter", schemaFilter));

        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
