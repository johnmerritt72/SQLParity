using SQLParity.Core;
using Xunit;

namespace SQLParity.Core.Tests;

public class SchemaReadOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IncludesSchema_NoFilter_IncludesEverything(string? filter)
    {
        var options = new SchemaReadOptions { SchemaFilter = filter };
        Assert.True(options.IncludesSchema("dbo"));
        Assert.True(options.IncludesSchema("sales"));
    }

    [Fact]
    public void IncludesSchema_FilterSet_MatchesOnlyThatSchema()
    {
        var options = new SchemaReadOptions { SchemaFilter = "sales" };
        Assert.True(options.IncludesSchema("sales"));
        Assert.False(options.IncludesSchema("dbo"));
    }

    [Fact]
    public void IncludesSchema_IsCaseInsensitive()
    {
        var options = new SchemaReadOptions { SchemaFilter = "SALES" };
        Assert.True(options.IncludesSchema("sales"));
    }

    [Fact]
    public void IncludesSchema_TrimsFilterWhitespace()
    {
        var options = new SchemaReadOptions { SchemaFilter = " sales " };
        Assert.True(options.IncludesSchema("sales"));
    }

    [Fact]
    public void SchemaFilter_DefaultsToNull()
    {
        Assert.Null(new SchemaReadOptions().SchemaFilter);
    }
}
