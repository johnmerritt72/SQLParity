using System.Collections.Generic;
using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class TableDdlParserTests
{
    private static TableDdlParser MakeParser() => new TableDdlParser();

    [Fact]
    public void Parse_returns_null_and_warning_on_syntax_error()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "THIS IS NOT VALID T-SQL %%%",
            "dbo", "Broken", sourceDatabase: null,
            out IReadOnlyList<string> warnings);

        Assert.Null(result);
        Assert.NotEmpty(warnings);
        Assert.Contains("Could not parse", warnings[0]);
    }

    [Fact]
    public void Parse_returns_null_and_warning_when_no_create_table_present()
    {
        var parser = MakeParser();
        var result = parser.Parse(
            "SELECT 1",
            "dbo", "NoTable", sourceDatabase: null,
            out IReadOnlyList<string> warnings);

        Assert.Null(result);
        Assert.NotEmpty(warnings);
        Assert.Contains("No CREATE TABLE statement", warnings[0]);
    }
}
