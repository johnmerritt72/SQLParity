namespace SQLParity.Core.Model;

/// <summary>
/// Environment classification for a database connection side. Drives
/// color assignment and safety rules (e.g., PROD blocks live apply).
/// </summary>
public enum EnvironmentTag
{
    Untagged,
    Dev,
    Sandbox,
    Staging,
    Prod
}
