using System;

namespace SQLParity.Core
{
    /// <summary>
    /// Controls which object types are read during schema extraction.
    /// </summary>
    public class SchemaReadOptions
    {
        public bool IncludeSchemas { get; set; } = true;
        public bool IncludeTables { get; set; } = true;
        public bool IncludeViews { get; set; } = true;
        public bool IncludeStoredProcedures { get; set; } = true;
        public bool IncludeFunctions { get; set; } = true;
        public bool IncludeSequences { get; set; } = true;
        public bool IncludeSynonyms { get; set; } = true;
        public bool IncludeUserDefinedDataTypes { get; set; } = true;
        public bool IncludeUserDefinedTableTypes { get; set; } = true;

        /// <summary>
        /// Include object- and schema-level permission comparison. On by default.
        /// Only effective in live-vs-live comparisons (folder mode skips it).
        /// </summary>
        public bool IncludePermissions { get; set; } = true;

        /// <summary>
        /// When set, the read is limited to objects in this one schema
        /// (case-insensitive). Null or whitespace = read all schemas.
        /// </summary>
        public string? SchemaFilter { get; set; }

        /// <summary>
        /// True when the given schema passes <see cref="SchemaFilter"/> —
        /// i.e. no filter is set, or the names match case-insensitively.
        /// </summary>
        public bool IncludesSchema(string schemaName)
        {
            return string.IsNullOrWhiteSpace(SchemaFilter)
                || string.Equals(SchemaFilter.Trim(), schemaName, StringComparison.OrdinalIgnoreCase);
        }

        public static SchemaReadOptions All => new SchemaReadOptions();
    }
}
