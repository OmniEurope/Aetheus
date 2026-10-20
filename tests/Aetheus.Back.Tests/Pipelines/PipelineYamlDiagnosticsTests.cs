// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The "unknown property will be ignored" warning is derived from the definitions the strict
/// deserializer binds. Hand-kept lists had drifted: honoured fields were announced as ignored.
/// </summary>
public sealed class PipelineYamlDiagnosticsTests
{
    private static List<string> Warnings(string yaml)
    {
        var warnings = new List<string>();
        PipelineYamlDiagnostics.AppendUnknownPropertyWarnings(yaml, warnings, NullLogger.Instance);
        return warnings;
    }

    /// <summary>
    /// Every key the former lists knew, at each level, is still known - except a job's artifact_name,
    /// which the list claimed and the deserializer has always refused (the job definition has no such
    /// field): announcing it as merely ignored was the wrong message.
    /// </summary>
    [Fact]
    public void EveryKeyTheHandKeptListsKnew_IsStillKnown()
    {
        var warnings = Warnings("""
            name: p
            trigger: manual
            schedule: "0 0 2 * * *"
            source_branch: develop
            branches: [develop]
            supersede_running: true
            on_success: []
            extends: t@1
            parameters: []
            variables: {}
            variable_libraries: []
            vaults: []
            stages:
              - name: s
                agent: a
                os: linux
                group: g
                environment: e
                pool: p
                execution_role: deploy
                condition: "succeeded()"
                depends_on: []
                variables: {}
                artifacts: []
                artifact_name: n
                approval_timeout_minutes: 10
                remove: false
                jobs:
                  - name: j
                    agent: a
                    os: linux
                    pool: p
                    environment: e
                    execution_role: build
                    condition: "succeeded()"
                    variables: {}
                    artifacts: []
                    remove: false
                steps:
                  - name: st
                    shell: echo
                    condition: "succeeded()"
                    checkout: true
                    working_directory: w
                    timeout_seconds: 1
                    retry_count: 0
                    continue_on_error: false
                    type: lint
                    version: v
                    changelog: c
                    target_files: []
                    analysis_category: accessibility
                    pipeline: p
                    variables: {}
                    inherit_source: false
                    source_branch: b
                    source_commit: c
                    artifact: a
                    artifact_source_pipeline: p
                    artifact_source_selector: s
                    release: r
                    target_directory: d
                    allow_missing: false
                    app: a
                    compose: c
                    health_timeout_seconds: 1
                    health_url: u
                    backup_run: 1
                    min_coverage: 1
                    coverage_tool: t
                    coverage_language: l
                    coverage_version: v
                    max_complexity: 1
                    scanner: s
                    analysis_scope: quality
                    analysis_preset: p
                    analysis_rules: []
                    target_url: u
                    target_classification: c
                    active: false
                    api_specification_url: u
                    api_specification_format: f
                    config_files: {}
                    outputs: []
                    remove: false
            """);

        Assert.Empty(warnings);
    }

    [Fact]
    public void FieldsTheListsMissed_AreNoLongerAnnouncedAsIgnored()
    {
        Assert.Empty(Warnings("""
            name: p
            stages:
              - name: s
                steps:
                  - name: switch
                    type: bluegreen-switch
                    reload_helper: /usr/local/lib/x
                    confirm_minutes: "10"
                  - name: release
                    type: release
                    deployed: true
            """));
    }

    [Fact]
    public void AJobArtifactName_IsReported_AndSkippedByTheDeserializer()
    {
        const string yaml = """
            name: p
            stages:
              - name: s
                jobs:
                  - name: j
                    artifact_name: n
            """;

        Assert.Single(Warnings(yaml), warning => warning.Contains("artifact_name", StringComparison.Ordinal));
        // Unknown keys are skipped with explicit diagnostics.
        var definition = Aetheus.Back.Services.YamlParsingHelper.Deserializer
            .Deserialize<Aetheus.Shared.Components.Pipelines.PipelineYamlDefinition>(yaml);
        Assert.Equal("j", Assert.Single(Assert.Single(definition.Stages).Jobs).Name);
    }

    [Fact]
    public void AnUnknownKey_IsStillReported_AtItsLevel()
    {
        var warnings = Warnings("""
            name: p
            colour: blue
            stages:
              - name: s
                colour: blue
                steps:
                  - name: st
                    colour: blue
            """);

        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.Contains("colour", warning, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownKeyInANestedBlock_IsReported_WithItsPath()
    {
        var warnings = Warnings("""
            name: p
            isolation:
              mode: container
              colour: blue
            stages:
              - name: s
                steps:
                  - name: st
                    shell: echo
            """);

        Assert.Equal(["Unknown top-level > isolation property 'colour' will be ignored."], warnings);
    }
}
