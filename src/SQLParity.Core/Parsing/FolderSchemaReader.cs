using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Derives an object name from a .sql file's basename. Strips the extension,
    /// then returns everything after the last dot — supports both plain names
    /// ("MyProc.sql") and the schema-qualified convention used by folder-sync
    /// writers ("PROC_Db.schema.Name.sql" → "Name").
    /// </summary>
    public static string ExtractObjectNameFromFile(string fileNameOrPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileNameOrPath ?? string.Empty);
        if (string.IsNullOrEmpty(baseName)) return string.Empty;
        int lastDot = baseName.LastIndexOf('.');
        return lastDot >= 0 ? baseName.Substring(lastDot + 1) : baseName;
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
        var (parsedFiles, _) = ParseAllFiles(folderPath, recursive);

        // Single-DB legacy path: every parsed object goes into the same bucket
        // regardless of its TargetDatabase tag. Used by callers that haven't
        // been migrated to the multi-DB API and by single-DB tests.
        var fileEntries = parsedFiles
            .Select(pf => (pf.FilePath, Objects: (IReadOnlyList<ParsedSqlObject>)pf.Objects, TotalInFile: pf.Objects.Count))
            .ToList();
        var allWarnings = parsedFiles.SelectMany(pf => pf.Warnings).ToList();
        return BuildReadResult(folderPath, serverName, databaseName, fileEntries, allWarnings);
    }

    /// <summary>
    /// Buckets parsed objects by their effective <c>TargetDatabase</c> (the
    /// most recent <c>USE [Db]</c> in the file, or <paramref name="defaultDatabase"/>
    /// when no USE is present) and returns one <see cref="FolderReadResult"/>
    /// per database. The host VM uses this to drive a multi-DB Side A
    /// comparison: each returned key is a database name to read from the
    /// live server, and each value is the corresponding folder-side schema +
    /// per-object source-file mapping.
    /// </summary>
    /// <remarks>
    /// Databases that appear only inside an overruled USE statement (one
    /// shadowed by a later USE before any CREATE) are NOT keys in the result —
    /// they're filtered out by the parser and never tagged on any object.
    /// </remarks>
    public IReadOnlyDictionary<string, FolderReadResult> ReadFolderByDatabase(
        string folderPath,
        string serverName,
        string defaultDatabase,
        bool recursive = false)
    {
        if (string.IsNullOrEmpty(defaultDatabase))
            throw new ArgumentException("Default database is required.", nameof(defaultDatabase));

        var (parsedFiles, _) = ParseAllFiles(folderPath, recursive);

        // Group each file's objects by effective DB. A single file with two
        // USE statements that each route to a different DB will contribute
        // entries to two different buckets.
        var perDb = new Dictionary<string, List<(string FilePath, List<ParsedSqlObject> Objects, int TotalInFile)>>(
            StringComparer.OrdinalIgnoreCase);
        var perDbWarnings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pf in parsedFiles)
        {
            bool fileContributedToAnyBucket = false;
            int totalInFile = pf.Objects.Count;
            foreach (var group in pf.Objects.GroupBy(
                o => o.TargetDatabase ?? defaultDatabase, StringComparer.OrdinalIgnoreCase))
            {
                fileContributedToAnyBucket = true;
                if (!perDb.TryGetValue(group.Key, out var list))
                {
                    list = new List<(string, List<ParsedSqlObject>, int)>();
                    perDb[group.Key] = list;
                }
                list.Add((pf.FilePath, group.ToList(), totalInFile));

                if (pf.Warnings.Count > 0)
                {
                    if (!perDbWarnings.TryGetValue(group.Key, out var ws))
                    {
                        ws = new List<string>();
                        perDbWarnings[group.Key] = ws;
                    }
                    ws.AddRange(pf.Warnings);
                }
            }

            // A file that yielded no parsed objects (e.g. a read-error file or
            // a USE-only file that hit an overrule warning) still has warnings
            // worth surfacing. Attribute them to the defaultDatabase bucket so
            // the user sees them; create the bucket if needed.
            if (!fileContributedToAnyBucket && pf.Warnings.Count > 0)
            {
                if (!perDbWarnings.TryGetValue(defaultDatabase, out var ws))
                {
                    ws = new List<string>();
                    perDbWarnings[defaultDatabase] = ws;
                }
                ws.AddRange(pf.Warnings);
                if (!perDb.ContainsKey(defaultDatabase))
                    perDb[defaultDatabase] = new List<(string, List<ParsedSqlObject>, int)>();
            }
        }

        var result = new Dictionary<string, FolderReadResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dbName, fileEntries) in perDb.Select(kv => (kv.Key, kv.Value)))
        {
            perDbWarnings.TryGetValue(dbName, out var ws);
            var entries = fileEntries
                .Select(e => (e.FilePath, (IReadOnlyList<ParsedSqlObject>)e.Objects, e.TotalInFile))
                .ToList();
            result[dbName] = BuildReadResult(folderPath, serverName, dbName,
                entries, ws ?? new List<string>());
        }
        return result;
    }

    private (List<ParsedFile> Files, List<string> GlobalWarnings) ParseAllFiles(
        string folderPath, bool recursive)
    {
        if (string.IsNullOrEmpty(folderPath))
            throw new ArgumentException("Folder path is required.", nameof(folderPath));
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        // recursive parameter intentionally ignored in v1.2 — only root-level
        // *.sql files are read. Wired through so v1.3 subfolder support is a
        // non-breaking addition.
        var sqlFiles = Directory.EnumerateFiles(folderPath, "*.sql", SearchOption.TopDirectoryOnly);

        var files = new List<ParsedFile>();
        var globalWarnings = new List<string>();
        foreach (var file in sqlFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex)
            {
                files.Add(new ParsedFile(file, Array.Empty<ParsedSqlObject>(),
                    new[] { $"Could not read '{Path.GetFileName(file)}': {ex.Message}" }));
                continue;
            }

            var parsed = _parser.Parse(text, out var parseWarnings);
            var prefixed = parseWarnings
                .Select(w => $"{Path.GetFileName(file)}: {w}")
                .ToList();
            files.Add(new ParsedFile(file, parsed, prefixed));
        }
        return (files, globalWarnings);
    }

    private static FolderReadResult BuildReadResult(
        string folderPath,
        string serverName,
        string databaseName,
        IReadOnlyList<(string FilePath, IReadOnlyList<ParsedSqlObject> Objects, int TotalInFile)> fileEntries,
        IReadOnlyList<string> initialWarnings)
    {
        var warnings = new List<string>(initialWarnings);

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

        foreach (var (filePath, parsedList, totalInFile) in fileEntries)
        {
            // IsSingleObjectFile reflects the source file's total CREATE count,
            // not the count for the current bucket. A multi-USE file with one
            // object per DB has totalInFile == 2, so neither bucket gets the
            // single-file optimization — the writer must split it.
            bool isSingleObjectFile = totalInFile == 1;
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
                    RemoveExistingEntry(obj.Id, tables, views, procs, functions,
                        sequences, synonyms, udts, tableTypes, schemas);
                }

                objectToFile[obj.Id] = new FileBacking
                {
                    FilePath = filePath,
                    IsSingleObjectFile = isSingleObjectFile,
                    FileName = isSingleObjectFile ? ExtractObjectNameFromFile(filePath) : null,
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

    private readonly struct ParsedFile
    {
        public ParsedFile(string filePath, IReadOnlyList<ParsedSqlObject> objects, IReadOnlyList<string> warnings)
        {
            FilePath = filePath;
            Objects = objects;
            Warnings = warnings;
        }
        public string FilePath { get; }
        public IReadOnlyList<ParsedSqlObject> Objects { get; }
        public IReadOnlyList<string> Warnings { get; }
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
