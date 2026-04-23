using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQLParity.Core.Project;

public static class ProjectFileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Save(ProjectFile project, string path)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (path is null) throw new ArgumentNullException(nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(project, Options);
        File.WriteAllText(path, json);
    }

    public static ProjectFile Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Project file not found: " + path, path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectFile>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize project file: " + path);
    }
}
