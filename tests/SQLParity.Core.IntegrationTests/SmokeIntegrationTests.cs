using Microsoft.Data.SqlClient;
using Xunit;

namespace SQLParity.Core.IntegrationTests;

public class SmokeIntegrationTests : IClassFixture<LocalDbConnectionFixture>
{
    private readonly LocalDbConnectionFixture _fixture;

    public SmokeIntegrationTests(LocalDbConnectionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CanQueryServerVersionFromLocalDb()
    {
        using var conn = new SqlConnection(_fixture.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        var version = cmd.ExecuteScalar() as string;

        Assert.NotNull(version);
        Assert.Contains("SQL Server", version);
    }
}
