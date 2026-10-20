// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;

namespace Aetheus.Back.Tests.Analysis;

public sealed class ZapAnalysisParserTests
{
    [Fact]
    public void Parse_NormalizesAlertsWithoutPersistingAttackOrEvidence()
    {
        const string json = """
            {"site":[{"alerts":[{"pluginid":"10020","alert":"Missing header","riskcode":"3","confidence":"2","desc":"<p>Header is absent</p>","cweid":"693","reference":"https://www.zaproxy.org/docs/alerts/10020/","instances":[{"uri":"https://qa.example.test/orders/42","method":"GET","param":"id","attack":"sensitive-payload","evidence":"sensitive-response"}]}]}]}
            """;

        var finding = Assert.Single(ZapAnalysisParser.Parse(json));

        Assert.Equal(AnalysisSeverity.High, finding.Severity);
        Assert.Equal(AnalysisCategory.Dast, finding.Category);
        Assert.Equal("/orders/42", finding.FilePath);
        Assert.DoesNotContain("sensitive", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AlertWithoutInstances_YieldsOneFindingWithoutLocation()
    {
        const string json = """
            {"site":[{"alerts":[{"pluginid":"10202","alert":"Absence of Anti-CSRF Tokens","riskcode":"2","confidence":"2","cweid":"352"}]}]}
            """;

        var finding = Assert.Single(ZapAnalysisParser.Parse(json));

        Assert.Equal("10202", finding.RuleId);
        Assert.Equal("CWE-352", finding.Cwe);
        Assert.Equal(AnalysisSeverity.Medium, finding.Severity);
        Assert.Null(finding.FilePath);
        Assert.Null(finding.Symbol);
    }

    [Fact]
    public void Parse_DropsInformationalNoiseButKeepsLowAndAbove()
    {
        const string json = """
            {"site":[{"alerts":[
              {"pluginid":"100000","alert":"Client error","riskcode":"0","instances":[{"uri":"https://qa.example.test/a","method":"GET"}]},
              {"pluginid":"10027","alert":"Suspicious comment","riskcode":"1","instances":[{"uri":"https://qa.example.test/app.js","method":"GET"}]}
            ]}]}
            """;

        var finding = Assert.Single(ZapAnalysisParser.Parse(json));

        Assert.Equal("10027", finding.RuleId);
        Assert.Equal(AnalysisSeverity.Low, finding.Severity);
    }

    [Fact]
    public void Parse_KeepsEachVariantOfARuleApart()
    {
        // Finding 870 (2026-10-03): every CSP variant at "/" shared one finding, titled after the first.
        const string json = """
            {"site":[{"alerts":[
              {"pluginid":"10055","alertRef":"10055-6","alert":"CSP: style-src unsafe-inline","riskcode":"2","cweid":"693","instances":[{"uri":"http://127.0.0.1:24001/","method":"GET","param":"Content-Security-Policy"}]},
              {"pluginid":"10055","alertRef":"10055-4","alert":"CSP: Wildcard Directive","riskcode":"2","cweid":"693","instances":[{"uri":"http://127.0.0.1:24001/","method":"GET","param":"Content-Security-Policy"}]}
            ]}]}
            """;

        var findings = ZapAnalysisParser.Parse(json);

        Assert.Equal(2, findings.Select(finding => finding.Fingerprint).Distinct().Count());
        Assert.Equal(["10055-6", "10055-4"], findings.Select(finding => finding.RuleId));
        Assert.Equal(["CSP: style-src unsafe-inline", "CSP: Wildcard Directive"], findings.Select(finding => finding.Title));
    }

    [Fact]
    public void Parse_KeepsTheFingerprintOfAnAlertWithoutVariant()
    {
        // Triage already recorded on plain alerts survives: alertRef equal to the plugin id changes nothing.
        const string withoutRef = """
            {"site":[{"alerts":[{"pluginid":"10021","alert":"X-Content-Type-Options Header Missing","riskcode":"1","cweid":"693","instances":[{"uri":"https://qa.example.test/app.js","method":"GET","param":"x-content-type-options"}]}]}]}
            """;
        var withRef = withoutRef.Replace("\"pluginid\":\"10021\"", "\"pluginid\":\"10021\",\"alertRef\":\"10021\"", StringComparison.Ordinal);

        var before = Assert.Single(ZapAnalysisParser.Parse(withoutRef));
        var after = Assert.Single(ZapAnalysisParser.Parse(withRef));

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal("10021", after.RuleId);
    }

    [Fact]
    public void Parse_DropsWeakTransportWarningOnlyForLoopbackHttpQa()
    {
        const string json = """
            {"site":[{"alerts":[{"pluginid":"10105","alert":"Weak Authentication Method","riskcode":"2","instances":[
              {"uri":"http://127.0.0.1:24001/api/packages","method":"GET"},
              {"uri":"https://qa.example.test/api/packages","method":"GET"}
            ]}]}]}
            """;

        var finding = Assert.Single(ZapAnalysisParser.Parse(json));

        Assert.Equal("/api/packages", finding.FilePath);
        Assert.Equal(AnalysisSeverity.Medium, finding.Severity);
    }
}
