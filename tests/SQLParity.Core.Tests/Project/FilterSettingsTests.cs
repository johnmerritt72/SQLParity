using SQLParity.Core.Model;
using SQLParity.Core.Project;
using Xunit;

namespace SQLParity.Core.Tests.Project;

public class FilterSettingsTests
{
    [Fact]
    public void DefaultFilters_IncludeEverything()
    {
        var filters = new FilterSettings();
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.View, "sales", "OrdersView"));
    }

    [Fact]
    public void IncludedObjectTypes_FiltersOut_ExcludedTypes()
    {
        var filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table, ObjectType.View },
        };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.View, "dbo", "V1"));
        Assert.False(filters.ShouldInclude(ObjectType.StoredProcedure, "dbo", "GetOrders"));
    }

    [Fact]
    public void ExcludedSchemas_FiltersOut_MatchingSchemas()
    {
        var filters = new FilterSettings { ExcludedSchemas = new[] { "audit" } };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "AUDIT", "Log"));
    }

    [Fact]
    public void IncludedSchemas_FiltersOut_NonMatchingSchemas()
    {
        var filters = new FilterSettings { IncludedSchemas = new[] { "dbo", "sales" } };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.True(filters.ShouldInclude(ObjectType.Table, "sales", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
    }

    [Fact]
    public void ExcludedSchemas_TakesPrecedence_OverIncludedSchemas()
    {
        var filters = new FilterSettings
        {
            IncludedSchemas = new[] { "dbo", "audit" },
            ExcludedSchemas = new[] { "audit" },
        };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "audit", "Log"));
    }

    [Fact]
    public void ExcludedNamePatterns_WildcardStar()
    {
        var filters = new FilterSettings { ExcludedNamePatterns = new[] { "tmp_*" } };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "tmp_Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "TMP_Orders"));
    }

    [Fact]
    public void ExcludedNamePatterns_WildcardSuffix()
    {
        var filters = new FilterSettings { ExcludedNamePatterns = new[] { "*_bak" } };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders_bak"));
    }

    [Fact]
    public void ExcludedNamePatterns_QuestionMark()
    {
        var filters = new FilterSettings { ExcludedNamePatterns = new[] { "T?" } };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "T1"));
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "T12"));
    }

    [Fact]
    public void AllFiltersCombined()
    {
        var filters = new FilterSettings
        {
            IncludedObjectTypes = new[] { ObjectType.Table },
            IncludedSchemas = new[] { "dbo" },
            ExcludedNamePatterns = new[] { "tmp_*" },
        };
        Assert.True(filters.ShouldInclude(ObjectType.Table, "dbo", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.View, "dbo", "V1"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "sales", "Orders"));
        Assert.False(filters.ShouldInclude(ObjectType.Table, "dbo", "tmp_Orders"));
    }
}
