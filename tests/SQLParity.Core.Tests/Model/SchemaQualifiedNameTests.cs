using SQLParity.Core.Model;
using Xunit;

namespace SQLParity.Core.Tests.Model;

public class SchemaQualifiedNameTests
{
    [Fact]
    public void TopLevel_EqualsBySchemaAndName()
    {
        var a = SchemaQualifiedName.TopLevel("dbo", "Orders");
        var b = SchemaQualifiedName.TopLevel("dbo", "Orders");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TopLevel_DifferentSchema_NotEqual()
    {
        var a = SchemaQualifiedName.TopLevel("dbo", "Orders");
        var b = SchemaQualifiedName.TopLevel("sales", "Orders");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Child_EqualsBySchemaParentAndName()
    {
        var a = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");
        var b = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Child_DifferentParent_NotEqual()
    {
        var a = SchemaQualifiedName.Child("dbo", "Orders", "Id");
        var b = SchemaQualifiedName.Child("dbo", "Products", "Id");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TopLevel_ToString_ReturnsSchemaQualified()
    {
        var name = SchemaQualifiedName.TopLevel("dbo", "Orders");

        Assert.Equal("[dbo].[Orders]", name.ToString());
    }

    [Fact]
    public void Child_ToString_IncludesParent()
    {
        var name = SchemaQualifiedName.Child("dbo", "Orders", "OrderId");

        Assert.Equal("[dbo].[Orders].[OrderId]", name.ToString());
    }

    [Fact]
    public void Comparison_IsCaseInsensitive()
    {
        var a = SchemaQualifiedName.TopLevel("DBO", "Orders");
        var b = SchemaQualifiedName.TopLevel("dbo", "ORDERS");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
