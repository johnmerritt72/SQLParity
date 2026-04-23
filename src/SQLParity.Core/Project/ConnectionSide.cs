using SQLParity.Core.Model;

namespace SQLParity.Core.Project;

/// <summary>
/// One side of a comparison as persisted in a project file. Contains
/// connection identity (server + database), the user-chosen label, and
/// the environment tag. Never contains credentials.
/// </summary>
public sealed class ConnectionSide
{
    public required string ServerName { get; init; }
    public required string DatabaseName { get; init; }
    public required string Label { get; init; }
    public required EnvironmentTag Tag { get; init; }
}
