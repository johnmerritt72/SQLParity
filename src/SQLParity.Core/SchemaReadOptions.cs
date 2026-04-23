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

        public static SchemaReadOptions All => new SchemaReadOptions();
    }
}
