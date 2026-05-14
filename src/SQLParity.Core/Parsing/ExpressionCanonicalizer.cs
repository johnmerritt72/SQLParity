using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLParity.Core.Parsing;

/// <summary>
/// Canonicalizes a single T-SQL expression or boolean predicate as it appears
/// in a DEFAULT or CHECK constraint definition. Strips redundant
/// <c>ParenthesisExpression</c> wrappers (SMO writes <c>((1))</c> into the
/// catalog for what the user wrote as <c>1</c>) and round-trips the AST through
/// <see cref="Sql160ScriptGenerator"/> so two semantically-equivalent strings
/// produce the same canonical text. Returns the input unchanged on parse
/// failure — comparison falls back to ordinal string equality, same as before.
/// </summary>
public static class ExpressionCanonicalizer
{
    /// <summary>Canonicalizes a scalar expression (e.g. a DEFAULT definition).</summary>
    public static string Canonicalize(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return string.Empty;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(expression);
        ScalarExpression? fragment = parser.ParseExpression(reader, out IList<ParseError> errors);
        if (errors != null && errors.Count > 0) return expression;
        if (fragment == null) return expression;

        fragment = StripParens(fragment);
        return Render(fragment, fallback: expression);
    }

    /// <summary>Canonicalizes a boolean predicate (e.g. a CHECK definition).</summary>
    public static string CanonicalizeBoolean(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return string.Empty;

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(expression);
        BooleanExpression? fragment = parser.ParseBooleanExpression(reader, out IList<ParseError> errors);
        if (errors != null && errors.Count > 0) return expression;
        if (fragment == null) return expression;

        // For boolean expressions, only strip OUTER parens (the whole-predicate
        // wrap that SMO adds). Inner parens around literals or operands are
        // left to ScriptGenerator's discretion.
        while (fragment is BooleanParenthesisExpression bpe && bpe.Expression != null)
            fragment = bpe.Expression;

        return Render(fragment, fallback: expression);
    }

    private static ScalarExpression StripParens(ScalarExpression expr)
    {
        while (expr is ParenthesisExpression pe && pe.Expression != null)
            expr = pe.Expression;
        return expr;
    }

    private static string Render(TSqlFragment fragment, string fallback)
    {
        var gen = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            SqlVersion = SqlVersion.Sql160,
            KeywordCasing = KeywordCasing.Lowercase,
            IncludeSemicolons = false,
            NewLineBeforeOpenParenthesisInMultilineList = false,
        });
        gen.GenerateScript(fragment, out string output);
        return string.IsNullOrEmpty(output) ? fallback : output.Trim();
    }
}
