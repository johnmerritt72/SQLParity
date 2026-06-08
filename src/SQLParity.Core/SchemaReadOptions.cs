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

        public static SchemaReadOptions All => new SchemaReadOptions();
    }
}
