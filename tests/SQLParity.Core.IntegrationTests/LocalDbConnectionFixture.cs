using Microsoft.Data.SqlClient;

namespace SQLParity.Core.IntegrationTests;

/// <summary>
/// xUnit fixture that provides a connection string to the local default
/// LocalDB instance and verifies it is reachable. Tests that need a real
/// SQL Server use this fixture so failures point at LocalDB rather than
/// at test logic.
/// </summary>
public sealed class LocalDbConnectionFixture
{
    public string ConnectionString { get; } =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

    public LocalDbConnectionFixture()
    {
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = cmd.ExecuteScalar();
        if (result is not int i || i != 1)
        {
            throw new System.InvalidOperationException(
                $"LocalDB smoke check returned unexpected value: {result}");
        }
    }
}
