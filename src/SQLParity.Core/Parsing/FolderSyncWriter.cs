using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLParity.Core.Model;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Writes the result of an A→B (database → folder) sync. For each Change in
/// the input, decides whether to overwrite an existing file, create a new
/// file with the naming-convention name, split a multi-object file into
/// per-object files (commenting out the original), or rewrite a file as a
/// guarded DROP for an object that's been removed from the source DB.
/// </summary>
/// <remarks>
/// Pure-ish: takes a fixed <c>nowUtc</c> so file headers are deterministic
/// in tests. Filesystem side effects are real <see cref="File.WriteAllText(string, string, Encoding)"/>
/// calls, so tests use a temp directory.
/// </remarks>
public sealed class FolderSyncWriter
{
    private readonly SqlFileParser _parser;
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public FolderSyncWriter() : this(new SqlFileParser()) { }
    public FolderSyncWriter(SqlFileParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public WriteManifest WriteChanges(
        IReadOnlyList<Change> changes,
        FolderSchemaContext context,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc)
    {
        if (changes is null) throw new ArgumentNullException(nameof(changes));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var updated = new List<string>();
        var created = new List<string>();
        var commentedOut = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();

        // Group multi-object-file changes by the file they touch — each such
        // file is split exactly once even if multiple changes target it.
        var multiByFile = new Dictionary<string, List<Change>>(StringComparer.OrdinalIgnoreCase);
        var modifiedSingle = new List<Change>();
        var droppedSingle = new List<Change>();
        var newOnA = new List<Change>();

        foreach (var c in changes)
        {
            if (c.Status == ChangeStatus.New)
            {
                newOnA.Add(c);
                continue;
            }

            if (!context.ObjectToFile.TryGetValue(c.Id, out var backing))
            {
                skipped.Add($"{c.Id}: no source file mapping; skipped.");
                continue;
            }

            if (backing.IsSingleObjectFile)
            {
                if (c.Status == ChangeStatus.Modified) modifiedSingle.Add(c);
                else if (c.Status == ChangeStatus.Dropped) droppedSingle.Add(c);
            }
            else
            {
                if (!multiByFile.TryGetValue(backing.FilePath, out var list))
                    multiByFile[backing.FilePath] = list = new List<Change>();
                list.Add(c);
            }
        }

        // 1) Modified objects in single-object files: overwrite in place.
        foreach (var c in modifiedSingle)
        {
            var backing = context.ObjectToFile[c.Id];
            try
            {
                string content = BuildCreateFile(c.ObjectType, c.Id, c.DdlSideA ?? string.Empty,
                    sourceServerName, sourceDatabaseName, nowUtc);
                File.WriteAllText(backing.FilePath, content, Utf8WithBom);
                updated.Add(backing.FilePath);
            }
            catch (Exception ex)
            {
                errors.Add($"Updating {backing.FilePath}: {ex.Message}");
            }
        }

        // 2) Dropped objects in single-object files: rewrite as drop file.
        foreach (var c in droppedSingle)
        {
            var backing = context.ObjectToFile[c.Id];
            try
            {
                string original = SafeReadAllText(backing.FilePath);
                string content = BuildDropFile(c.ObjectType, c.Id, original,
                    sourceServerName, sourceDatabaseName, nowUtc);
                File.WriteAllText(backing.FilePath, content, Utf8WithBom);
                updated.Add(backing.FilePath);
            }
            catch (Exception ex)
            {
                errors.Add($"Dropping {backing.FilePath}: {ex.Message}");
            }
        }

        // 3) Multi-object files: split each into per-object files, comment out original.
        foreach (var (filePath, fileChanges) in multiByFile.Select(kv => (kv.Key, kv.Value)))
        {
            try
            {
                SplitMultiObjectFile(filePath, fileChanges, context.FolderPath,
                    sourceServerName, sourceDatabaseName, nowUtc,
                    created, commentedOut, errors);
            }
            catch (Exception ex)
            {
                errors.Add($"Splitting {filePath}: {ex.Message}");
            }
        }

        // 4) New objects: create new files, resolving filename collisions.
        foreach (var c in newOnA)
        {
            try
            {
                string desired = NamingConvention.FilenameFor(c.ObjectType, c.Id);
                string fullPath = ResolveNewFilePath(context.FolderPath, desired);
                string content = BuildCreateFile(c.ObjectType, c.Id, c.DdlSideA ?? string.Empty,
                    sourceServerName, sourceDatabaseName, nowUtc);
                File.WriteAllText(fullPath, content, Utf8WithBom);
                created.Add(fullPath);
            }
            catch (Exception ex)
            {
                errors.Add($"Creating new file for {c.Id}: {ex.Message}");
            }
        }

        return new WriteManifest
        {
            FilesUpdated = updated,
            FilesCreated = created,
            FilesCommentedOut = commentedOut,
            Skipped = skipped,
            Errors = errors,
        };
    }

    private void SplitMultiObjectFile(
        string filePath,
        List<Change> fileChanges,
        string folderPath,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc,
        List<string> created,
        List<string> commentedOut,
        List<string> errors)
    {
        string original = File.ReadAllText(filePath);
        var parsed = _parser.Parse(original);
        var changesByid = fileChanges.ToDictionary(c => c.Id);

        var splitTargets = new List<string>();
        foreach (var obj in parsed)
        {
            string desired = NamingConvention.FilenameFor(obj.ObjectType, obj.Id);
            string targetPath = Path.Combine(folderPath, desired);
            // If the desired name collides with the original multi-file we're
            // splitting from, redirect to a numbered variant so we don't try
            // to overwrite the file mid-split.
            if (string.Equals(targetPath, filePath, StringComparison.OrdinalIgnoreCase))
                targetPath = ResolveNewFilePath(folderPath, desired, exclude: filePath);
            else if (File.Exists(targetPath))
                targetPath = ResolveNewFilePath(folderPath, desired, exclude: filePath);

            string content;
            if (changesByid.TryGetValue(obj.Id, out var change) && change.Status == ChangeStatus.Dropped)
            {
                // Object removed from DB — emit a drop file.
                content = BuildDropFile(obj.ObjectType, obj.Id, obj.Ddl,
                    sourceServerName, sourceDatabaseName, nowUtc);
            }
            else
            {
                string ddl = changesByid.TryGetValue(obj.Id, out var modChange) && modChange.Status == ChangeStatus.Modified
                    ? modChange.DdlSideA ?? string.Empty
                    : obj.Ddl;
                content = BuildCreateFile(obj.ObjectType, obj.Id, ddl,
                    sourceServerName, sourceDatabaseName, nowUtc);
            }

            File.WriteAllText(targetPath, content, Utf8WithBom);
            created.Add(targetPath);
            splitTargets.Add(Path.GetFileName(targetPath));
        }

        // Comment out the original file with a header noting where things went.
        var sb = new StringBuilder();
        sb.AppendLine($"-- Split into per-object files by SQLParity on {FormatTimestamp(nowUtc)}.");
        sb.AppendLine($"-- Source: {sourceServerName}/{sourceDatabaseName}");
        if (splitTargets.Count > 0)
        {
            sb.AppendLine("-- Replaced by:");
            foreach (var t in splitTargets) sb.AppendLine($"--   {t}");
        }
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine("/*");
        sb.AppendLine("-- Original file content (preserved for historical reference):");
        sb.AppendLine();
        sb.Append(original);
        if (!original.EndsWith("\n", StringComparison.Ordinal)) sb.AppendLine();
        sb.AppendLine("*/");

        File.WriteAllText(filePath, sb.ToString(), Utf8WithBom);
        commentedOut.Add(filePath);
    }

    private static string BuildCreateFile(
        ObjectType objectType,
        SchemaQualifiedName id,
        string bareDdl,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc)
    {
        string wrapped = IdempotentDdlWrapper.Wrap(objectType, id.Schema, id.Name, bareDdl);
        return BuildFile(objectType, id, wrapped, sourceServerName, sourceDatabaseName, nowUtc, isDrop: false);
    }

    private static string BuildDropFile(
        ObjectType objectType,
        SchemaQualifiedName id,
        string originalDdl,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc)
    {
        string drop = IdempotentDdlWrapper.WrapDrop(objectType, id.Schema, id.Name);

        var sb = new StringBuilder();
        sb.Append(BuildHeader(objectType, id, sourceServerName, sourceDatabaseName, nowUtc, isDrop: true));
        sb.AppendLine();
        sb.AppendLine(drop);
        sb.AppendLine("GO");
        if (!string.IsNullOrEmpty(originalDdl))
        {
            sb.AppendLine();
            sb.AppendLine("/*");
            sb.AppendLine("-- Historical reference — original definition before drop:");
            sb.AppendLine();
            sb.Append(originalDdl.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("*/");
        }
        return sb.ToString();
    }

    private static string BuildFile(
        ObjectType objectType,
        SchemaQualifiedName id,
        string wrappedBody,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc,
        bool isDrop)
    {
        var sb = new StringBuilder();
        sb.Append(BuildHeader(objectType, id, sourceServerName, sourceDatabaseName, nowUtc, isDrop));
        sb.AppendLine();
        sb.AppendLine(wrappedBody);
        sb.AppendLine("GO");
        return sb.ToString();
    }

    private static string BuildHeader(
        ObjectType objectType,
        SchemaQualifiedName id,
        string sourceServerName,
        string sourceDatabaseName,
        DateTime nowUtc,
        bool isDrop)
    {
        string typeLabel = NamingConvention.TypePrefix(objectType);
        string objectLabel = objectType == ObjectType.Schema
            ? $"{typeLabel} {id.Name}"
            : $"{typeLabel} {id.Schema}.{id.Name}";
        string note = isDrop
            ? "-- Re-running this script will drop the object if it still exists."
            : "-- Re-running this script is safe — see the IF (NOT) EXISTS / CREATE OR ALTER guard below.";

        // Two GOs: one after USE (standard) and one after the SQLParity header
        // comment block. The second is critical for module objects: without it,
        // SQL Server stores our prelude as part of sys.sql_modules.definition
        // and it re-appears on every subsequent script-out.
        var sb = new StringBuilder();
        sb.AppendLine($"USE [{sourceDatabaseName}];");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine($"-- Generated by SQLParity from {sourceServerName}/{sourceDatabaseName} on {FormatTimestamp(nowUtc)}.");
        sb.AppendLine($"-- Object: {objectLabel}{(isDrop ? "  (REMOVED from source database)" : string.Empty)}");
        sb.AppendLine(note);
        sb.AppendLine("GO");
        return sb.ToString();
    }

    private static string FormatTimestamp(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    private static string ResolveNewFilePath(string folder, string desiredName, string? exclude = null)
    {
        string candidate = Path.Combine(folder, desiredName);
        bool Excluded(string p) => exclude != null && string.Equals(p, exclude, StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(candidate) || Excluded(candidate))
            return candidate;

        string baseName = Path.GetFileNameWithoutExtension(desiredName);
        string ext = Path.GetExtension(desiredName);
        for (int n = 1; ; n++)
        {
            candidate = Path.Combine(folder, $"{baseName}_{n}{ext}");
            if (!File.Exists(candidate) || Excluded(candidate))
                return candidate;
        }
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }
}

/// <summary>Filename rules for folder mode (per spec §4.4).</summary>
internal static class NamingConvention
{
    public static string TypePrefix(ObjectType t) => t switch
    {
        ObjectType.StoredProcedure => "PROC",
        ObjectType.UserDefinedFunction => "FUNC",
        ObjectType.View => "VIEW",
        ObjectType.Table => "TABLE",
        ObjectType.Trigger => "TRIG",
        ObjectType.UserDefinedDataType => "TYPE",
        ObjectType.UserDefinedTableType => "TYPE",
        ObjectType.Sequence => "SEQ",
        ObjectType.Synonym => "SYN",
        ObjectType.Schema => "SCHEMA",
        _ => "OBJ",
    };

    public static string FilenameFor(ObjectType t, SchemaQualifiedName id)
    {
        string prefix = TypePrefix(t);
        return t == ObjectType.Schema
            ? $"{prefix}.{id.Name}.sql"
            : $"{prefix}.{id.Schema}.{id.Name}.sql";
    }
}
