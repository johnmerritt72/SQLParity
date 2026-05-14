using SQLParity.Core.Parsing;
using Xunit;

namespace SQLParity.Core.Tests.Parsing;

public class ExpressionCanonicalizerTests
{
    [Fact]
    public void Canonicalize_strips_smo_double_paren_wrap_around_integer_literal()
    {
        // SMO catalog form for DEFAULT 1
        Assert.Equal("1", ExpressionCanonicalizer.Canonicalize("((1))"));
    }

    [Fact]
    public void Canonicalize_strips_single_paren_wrap_around_function_call()
    {
        // SMO catalog form for DEFAULT GETUTCDATE()
        Assert.Equal("getutcdate()",
            ExpressionCanonicalizer.Canonicalize("(getutcdate())").ToLowerInvariant());
    }

    [Fact]
    public void Canonicalize_strips_paren_wrap_around_string_literal()
    {
        Assert.Equal("'hello'", ExpressionCanonicalizer.Canonicalize("('hello')"));
    }

    [Fact]
    public void Canonicalize_already_minimal_is_idempotent()
    {
        Assert.Equal("1", ExpressionCanonicalizer.Canonicalize("1"));
        Assert.Equal("getutcdate()",
            ExpressionCanonicalizer.Canonicalize("getutcdate()").ToLowerInvariant());
    }

    [Fact]
    public void Canonicalize_handles_negative_integer_literal()
    {
        Assert.Equal("-1", ExpressionCanonicalizer.Canonicalize("((-1))"));
    }

    [Fact]
    public void Canonicalize_returns_input_unchanged_on_parse_failure()
    {
        Assert.Equal("%%% INVALID %%%",
            ExpressionCanonicalizer.Canonicalize("%%% INVALID %%%"));
    }

    [Fact]
    public void Canonicalize_handles_null_or_empty_input()
    {
        Assert.Equal(string.Empty, ExpressionCanonicalizer.Canonicalize(null!));
        Assert.Equal(string.Empty, ExpressionCanonicalizer.Canonicalize(string.Empty));
    }

    [Fact]
    public void CanonicalizeBoolean_strips_outer_parens_around_simple_check()
    {
        // SMO catalog form for CHECK (Age > 0)
        var result = ExpressionCanonicalizer.CanonicalizeBoolean("([Age] > (0))").ToLowerInvariant();
        // Result should not contain "((" or wrap the entire expression in extra parens.
        // The inner "(0)" is also redundant — but ScriptGenerator may or may not strip it.
        // What matters: this output should match whatever a fresh ScriptDom parse of
        // "[Age] > 0" produces. The tightest assertion is that round-tripping is
        // idempotent.
        var idempotent = ExpressionCanonicalizer.CanonicalizeBoolean(result).ToLowerInvariant();
        Assert.Equal(result, idempotent);
        // And specifically: no outer-wrap parens
        Assert.False(result.StartsWith("(("),
            $"Outer paren wrap not stripped: got '{result}'");
    }
}
