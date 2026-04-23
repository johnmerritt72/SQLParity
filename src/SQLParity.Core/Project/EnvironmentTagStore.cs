using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

public sealed class EnvironmentTagStore
{
    private readonly string _filePath;
    private Dictionary<string, EnvironmentTag> _tags;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public EnvironmentTagStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _tags = LoadFromDisk();
    }

    public EnvironmentTag GetTag(string server, string database)
    {
        var key = MakeKey(server, database);
        return _tags.TryGetValue(key, out var tag) ? tag : EnvironmentTag.Untagged;
    }

    public void SetTag(string server, string database, EnvironmentTag tag)
    {
        var key = MakeKey(server, database);
        _tags[key] = tag;
        SaveToDisk();
    }

    public static EnvironmentTag SuggestTagFromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return EnvironmentTag.Untagged;

        var upper = label.ToUpperInvariant();

        if (upper.Contains("PROD")) return EnvironmentTag.Prod;
        if (upper.Contains("STAG")) return EnvironmentTag.Staging;
        if (upper.Contains("DEV")) return EnvironmentTag.Dev;
        if (upper.Contains("SANDBOX")) return EnvironmentTag.Sandbox;

        return EnvironmentTag.Untagged;
    }

    private static string MakeKey(string server, string database)
        => server.ToUpperInvariant() + "|" + database.ToUpperInvariant();

    private Dictionary<string, EnvironmentTag> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, EnvironmentTag>>(json, JsonOptions)
                ?? new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, EnvironmentTag>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_tags, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
