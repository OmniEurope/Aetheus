// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Seeding;

/// <summary>Versioned demo pipeline definitions shared by database and Git seeders.</summary>
internal static class DemoSeedDefinitions
{
    // Demo seed only (never run by default), but the no-fake rule still applies: build writes a real
    // artifact and deploy verifies it exists before copying it.
    public static string DemoPipelineYaml(string slug) => $"""
        name: demo-{slug}
        trigger: manual
        stages:
          - name: build
            steps:
              - name: compile
                shell: mkdir -p dist && printf 'demo-{slug}\n' > dist/index.html && ls -l dist
          - name: deploy
            depends_on: [build]
            steps:
              - name: ship
                shell: test -f dist/index.html && cp dist/index.html dist/deployed-{slug}.html && cat dist/deployed-{slug}.html
        """;

    public static string TotoOrchestrationYaml() => """
        name: toto-orchestration-accept
        trigger: manual
        stages:
          - name: orchestration
            steps:
              - name: trigger-ci
                type: trigger
                pipeline: toto-ci
              - name: trigger-qa
                type: trigger
                pipeline: toto-qa
        """;

    public static string TotoPipelineYaml(string name, string trigger) => $"""
        name: toto-{name}
        trigger: {trigger}
        {(trigger == "schedule" ? "schedule: '0 0 2 * * *'" : string.Empty)}
        stages:
          - name: verify
            steps:
              - name: check
                shell: mkdir -p artifacts && printf 'toto-{name}\n' > artifacts/result.txt && test -s artifacts/result.txt
        """;

    public static IReadOnlyDictionary<string, string> TotoPipelineDefinitions() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["toto-orchestration-accept"] = TotoOrchestrationYaml(),
            ["toto-ci"] = TotoPipelineYaml("ci", "webhook"),
            ["toto-qa"] = TotoPipelineYaml("qa", "schedule")
        };
}
