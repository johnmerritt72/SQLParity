using System;
using System.Collections.Generic;
using System.IO;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Reads a folder of .sql files and produces a <see cref="DatabaseSchema"/>
/// equivalent to what <c>SchemaReader</c> would produce from a live database.
/// Tables, sequences, synonyms, UDDTs, and UDTTs get their structured fields
/// stubbed (empty columns, empty base type, etc.) — folder mode does
/// DDL-text-only diff for v1.2; structural decomposition of CREATE TABLE
/// is deferred.
/// </summary>
public sealed class FolderSchemaReader
{
    private readonly SqlFileParser _parser;

    public FolderSchemaReader() : this(new SqlFileParser()) { }

    public FolderSchemaReader(SqlFileParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>
    /// Scans <paramref name="folderPath"/> for *.sql files and parses each.
    /// </summary>
    /// <param name="folderPath">Absolute folder path. Must exist.</param>
    /// <param name="serverName">Display label for ServerName on the schema.</param>
    /// <param name="databaseName">Display label for DatabaseName on the schema.</param>
    /// <param name="recursive">
    /// Reserved. v1.2 always treats this as false — only root-level *.sql
    /// files are read. Wired through so the v1.3 recursive feature can be
    /// added without a signature change.
    /// </param>
    public FolderReadResult ReadFolder(
        string folderPath,
        string serverName,
        string databaseName,
        bool recursive = false)
    {
        if (string.IsNullOrEmpty(folderPath))
            throw new ArgumentException("Folder path is required.", nameof(folderPath));
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        // recursive parameter intentionally ignored in v1.2 (see <param> doc above).
        var sqlFiles = Directory.EnumerateFiles(folderPath, "*.sql", SearchOption.TopDirectoryOnly);

        var warnings = new List<string>();
        var allParsedByFile = new List<(string FilePath, IReadOnlyList<ParsedSqlObject> Objects)>();

        foreach (var file in sqlFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read '{Path.GetFileName(file)}': {ex.Message}");
                continue;
            }

            var parsed = _parser.Parse(text);
            allParsedByFile.Add((file, parsed));
        }

        var tables = new List<TableModel>();
        var views = new List<ViewModel>();
        var procs = new List<StoredProcedureModel>();
        var functions = new List<UserDefinedFunctionModel>();
        var sequences = new List<SequenceModel>();
        var synonyms = new List<SynonymModel>();
        var udts = new List<UserDefinedDataTypeModel>();
        var tableTypes = new List<UserDefinedTableTypeModel>();
        var schemas = new List<SchemaModel>();

        var objectToFile = new Dictionary<SchemaQualifiedName, FileBacking>();
        var seenIds = new HashSet<SchemaQualifiedName>();

        foreach (var (filePath, parsedList) in allParsedByFile)
        {
            bool isSingleObjectFile = parsedList.Count == 1;
            foreach (var obj in parsedList)
            {
                if (!seenIds.Add(obj.Id))
                {
                    var existingPath = objectToFile.TryGetValue(obj.Id, out var existing)
                        ? Path.GetFileName(existing.FilePath)
                        : "(unknown)";
                    warnings.Add(
                        $"Duplicate object {obj.Id} found in '{Path.GetFileName(filePath)}' " +
                        $"(also defined in '{existingPath}'). Last definition wins.");
                    // Remove the older entry from the per-type lists so the new one wins.
                    RemoveExistingEntry(obj.Id, tables, views, procs, functions,
                        sequences, synonyms, udts, tableTypes, schemas);
                }

                objectToFile[obj.Id] = new FileBacking
                {
                    FilePath = filePath,
                    IsSingleObjectFile = isSingleObjectFile,
                };

                AddToBucket(obj, tables, views, procs, functions,
                    sequences, synonyms, udts, tableTypes, schemas);
            }
        }

        var schema = new DatabaseSchema
        {
            ServerName = serverName,
            DatabaseName = databaseName,
            ReadAtUtc = DateTime.UtcNow,
            Schemas = schemas,
            Tables = tables,
            Views = views,
            StoredProcedures = procs,
            Functions = functions,
            Sequences = sequences,
            Synonyms = synonyms,
            UserDefinedDataTypes = udts,
            UserDefinedTableTypes = tableTypes,
        };

        var context = new FolderSchemaContext
        {
            ObjectToFile = objectToFile,
            ParseWarnings = warnings,
            FolderPath = folderPath,
        };

        return new FolderReadResult { Schema = schema, Context = context };
    }

    private static void AddToBucket(
        ParsedSqlObject obj,
        List<TableModel> tables,
        List<ViewModel> views,
        List<StoredProcedureModel> procs,
        List<UserDefinedFunctionModel> functions,
        List<SequenceModel> sequences,
        List<SynonymModel> synonyms,
        List<UserDefinedDataTypeModel> udts,
        List<UserDefinedTableTypeModel> tableTypes,
        List<SchemaModel> schemas)
    {
        switch (obj.ObjectType)
        {
            case ObjectType.Table:
                tables.Add(new TableModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    Ddl = obj.Ddl,
                    Columns = Array.Empty<ColumnModel>(),
                    Indexes = Array.Empty<IndexModel>(),
                    ForeignKeys = Array.Empty<ForeignKeyModel>(),
                    CheckConstraints = Array.Empty<CheckConstraintModel>(),
                    Triggers = Array.Empty<TriggerModel>(),
                });
                break;
            case ObjectType.View:
                views.Add(new ViewModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    IsSchemaBound = false,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.StoredProcedure:
                procs.Add(new StoredProcedureModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.UserDefinedFunction:
                functions.Add(new UserDefinedFunctionModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    Kind = FunctionKind.Scalar,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.Sequence:
                sequences.Add(new SequenceModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    DataType = string.Empty,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.Synonym:
                synonyms.Add(new SynonymModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    BaseObject = string.Empty,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.UserDefinedDataType:
                udts.Add(new UserDefinedDataTypeModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    BaseType = string.Empty,
                    MaxLength = 0,
                    IsNullable = false,
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.UserDefinedTableType:
                tableTypes.Add(new UserDefinedTableTypeModel
                {
                    Id = obj.Id,
                    Schema = obj.Id.Schema,
                    Name = obj.Id.Name,
                    Columns = Array.Empty<ColumnModel>(),
                    Ddl = obj.Ddl,
                });
                break;
            case ObjectType.Schema:
                schemas.Add(new SchemaModel
                {
                    Name = obj.Id.Name,
                    Owner = string.Empty,
                    Ddl = obj.Ddl,
                });
                break;
            // Triggers, indexes, FKs, check constraints come from CREATE TABLE
            // sub-elements in DB mode; in folder mode we treat top-level
            // CREATE TRIGGER as part of the parent table's text. Top-level
            // CREATE TRIGGER batches in standalone files are not added to a
            // dedicated bucket here (no DatabaseSchema list for them — they
            // ride along with their table). Drop silently for v1.2.
            case ObjectType.Trigger:
            case ObjectType.Index:
            case ObjectType.ForeignKey:
            case ObjectType.CheckConstraint:
                break;
        }
    }

    private static void RemoveExistingEntry(
        SchemaQualifiedName id,
        List<TableModel> tables,
        List<ViewModel> views,
        List<StoredProcedureModel> procs,
        List<UserDefinedFunctionModel> functions,
        List<SequenceModel> sequences,
        List<SynonymModel> synonyms,
        List<UserDefinedDataTypeModel> udts,
        List<UserDefinedTableTypeModel> tableTypes,
        List<SchemaModel> schemas)
    {
        tables.RemoveAll(x => x.Id.Equals(id));
        views.RemoveAll(x => x.Id.Equals(id));
        procs.RemoveAll(x => x.Id.Equals(id));
        functions.RemoveAll(x => x.Id.Equals(id));
        sequences.RemoveAll(x => x.Id.Equals(id));
        synonyms.RemoveAll(x => x.Id.Equals(id));
        udts.RemoveAll(x => x.Id.Equals(id));
        tableTypes.RemoveAll(x => x.Id.Equals(id));
        schemas.RemoveAll(x => string.Equals(x.Name, id.Name, StringComparison.OrdinalIgnoreCase));
    }
}
