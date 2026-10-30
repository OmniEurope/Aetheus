// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The environment sample is rendered into the dotenv file Compose reads (ADR-057): every value
/// reaches Compose exactly as stored, and a value that is missing never becomes an empty setting.</summary>
public sealed class PipelineEnvTemplateRendererTests
{
    private static Dictionary<string, string> Variables(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ValuesAreWrittenInDoubleQuotes_WithBackslashQuoteAndDollarEscaped()
    {
        var rendered = PipelineEnvTemplateRenderer.Render(
            "DB_PASSWORD=#{DB_PASSWORD}#\nPLAIN=#{PLAIN}#\n",
            Variables(("DB_PASSWORD", "a$b\"c#d\\e f"), ("PLAIN", "plain-value=with=equals")));

        Assert.Equal("DB_PASSWORD=\"a\\$b\\\"c#d\\\\e f\"\nPLAIN=\"plain-value=with=equals\"\n", rendered);
    }

    [Fact]
    public void ALineMixingTextAndPlaceholders_IsRenderedAndQuotedAsOneValue()
    {
        var rendered = PipelineEnvTemplateRenderer.Render(
            "ConnectionStrings__Default=Host=db;Password=#{DB_PASSWORD}#;User=#{DB_USER}#\n",
            Variables(("DB_PASSWORD", "p@ss"), ("DB_USER", "app")));

        Assert.Equal("ConnectionStrings__Default=\"Host=db;Password=p@ss;User=app\"\n", rendered);
    }

    [Fact]
    public void CommentsAndBlankLines_PassThroughUnchanged()
    {
        var rendered = PipelineEnvTemplateRenderer.Render(
            "# a comment naming #{NOT_A_VARIABLE}# is not read\n\nAPPNAME=#{APPNAME}#\n",
            Variables(("APPNAME", "wordmorrow")));

        Assert.Equal("# a comment naming #{NOT_A_VARIABLE}# is not read\n\nAPPNAME=\"wordmorrow\"\n", rendered);
    }

    [Fact]
    public void AnUndefinedVariable_FailsInsteadOfWritingAnEmptySetting()
    {
        var failure = Assert.Throws<BadRequestException>(() =>
            PipelineEnvTemplateRenderer.Render("JWT_KEY=#{JWT_KEY}#\n", Variables(("OTHER", "x"))));

        Assert.Contains("JWT_KEY", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not defined", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMissingAndEmptyVariable_IsReportedInTheSameRefusal()
    {
        var failure = Assert.Throws<BadRequestException>(() =>
            PipelineEnvTemplateRenderer.Render(
                "APPNAME=#{APPNAME}#\nDB_USER=#{DB_USER}#\nDB_NAME=#{DB_USER}#\nJWT_KEY=#{JWT_KEY}#\nADMIN=#{ADMIN_EMAIL}#\nOK=#{OK}#\n",
                Variables(("OK", "fine"), ("ADMIN_EMAIL", ""))));

        Assert.Contains("'APPNAME', 'DB_USER', 'JWT_KEY'", failure.Message, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(failure.Message, "'DB_USER'"));
        Assert.Contains("empty: 'ADMIN_EMAIL'", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'OK'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("vault", failure.Message, StringComparison.Ordinal);
        Assert.Contains("environment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyVariable_Fails_BecauseComposeWouldReadItAsMissing()
    {
        var failure = Assert.Throws<BadRequestException>(() =>
            PipelineEnvTemplateRenderer.Render("ADMIN_EMAIL=#{ADMIN_EMAIL}#\n", Variables(("ADMIN_EMAIL", ""))));

        Assert.Contains("ADMIN_EMAIL", failure.Message, StringComparison.Ordinal);
        Assert.Contains("empty", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("with\rreturn")]
    [InlineData("with\0nul")]
    public void AValueWithALineBreak_IsRefused_BecauseItCouldAddAnotherSetting(string value)
    {
        Assert.Throws<BadRequestException>(() =>
            PipelineEnvTemplateRenderer.Render("X=#{X}#\n", Variables(("X", value))));
    }

    [Theory]
    [InlineData("NOT A KEY=#{X}#")]
    [InlineData("=#{X}#")]
    [InlineData("just a line")]
    public void AMalformedLine_IsRefused(string line)
    {
        Assert.Throws<BadRequestException>(() =>
            PipelineEnvTemplateRenderer.Render(line + "\n", Variables(("X", "v"))));
    }

    [Fact]
    public void ReferencedNames_ListsTheNamesOfTheValueLinesOnly()
    {
        var names = PipelineEnvTemplateRenderer.ReferencedNames(
            "# not #{IGNORED}#\nA=#{DB_PASSWORD}#\nB=x#{DB_PASSWORD}#y#{JWT_KEY}#\n");

        Assert.Equal(["DB_PASSWORD", "JWT_KEY"], names);
    }
}
