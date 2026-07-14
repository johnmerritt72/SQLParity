using SQLParity.Core;
using Xunit;

namespace SQLParity.Core.Tests;

public class SchemaCacheKeyTests
{
    [Fact]
    public void Make_NoSchemaFilter_IsServerPipeDatabase()
    {
        Assert.Equal("srv|db", SchemaCacheKey.Make("srv", "db"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Make_NullOrWhitespaceFilter_SameAsFullKey(string? filter)
    {
        Assert.Equal(SchemaCacheKey.Make("srv", "db"), SchemaCacheKey.Make("srv", "db", filter));
    }

    [Fact]
    public void Make_WithSchemaFilter_DiffersFromFullKey()
    {
        Assert.NotEqual(SchemaCacheKey.Make("srv", "db"), SchemaCacheKey.Make("srv", "db", "sales"));
        Assert.Equal("srv|db|sales", SchemaCacheKey.Make("srv", "db", "sales"));
    }

    [Fact]
    public void Make_TrimsAllParts()
    {
        Assert.Equal("srv|db|sales", SchemaCacheKey.Make(" srv ", " db ", " sales "));
    }

    [Fact]
    public void Make_NullServerAndDatabase_DoesNotThrow()
    {
        Assert.Equal("|", SchemaCacheKey.Make(null, null));
    }
}
