using System;
using Microsoft.Data.SqlClient;

namespace SQLParity.Core.IntegrationTests;

/// <summary>
/// Creates a throwaway database on LocalDB with a unique name. Drops it on
/// dispose. Tests that need a known schema subclass this and override
/// <see cref="SetupSql"/> to provide the DDL to run after creation.
/// </summary>
public abstract class ThrowawayDatabaseFixture : IDisposable
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;";

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    protected ThrowawayDatabaseFixture()
    {
        DatabaseName = "SQLParity_Test_" + Guid.NewGuid().ToString("N")[..8];
        ConnectionString = $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true;";

        ExecuteNonQuery(MasterConnectionString, $"CREATE DATABASE [{DatabaseName}]");

        var setupSql = SetupSql();
        if (!string.IsNullOrWhiteSpace(setupSql))
        {
            var batches = setupSql.Split(
                new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var batch in batches)
            {
                var trimmed = batch.Trim();
                if (trimmed.Length > 0)
                    ExecuteNonQuery(ConnectionString, trimmed);
            }
        }
    }

    protected abstract string SetupSql();

    public void Dispose()
    {
        try
        {
            ExecuteNonQuery(MasterConnectionString,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]");
        }
        catch
        {
            // Best effort cleanup
        }
        GC.SuppressFinalize(this);
    }

    private static void ExecuteNonQuery(string connectionString, string sql)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
