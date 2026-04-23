namespace SQLParity.Core
{
    /// <summary>
    /// Reports progress during schema reading.
    /// </summary>
    public class SchemaReadProgress
    {
        public string CurrentOperation { get; set; } = string.Empty;
        public int CompletedItems { get; set; }
        public int TotalItems { get; set; }
        public double PercentComplete => TotalItems > 0 ? (double)CompletedItems / TotalItems * 100 : 0;
    }
}
