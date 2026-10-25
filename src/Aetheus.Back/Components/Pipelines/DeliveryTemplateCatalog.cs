// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The delivery pipeline templates Aetheus ships, and the embedded resources that hold their
/// YAML. The seeder writes them to the catalog; the template repository reads the names.</summary>
internal static class DeliveryTemplateCatalog
{
    private const string ResourceRoot = "DeliveryPipelineTemplates/";

    /// <summary>The organization the shipped templates are seeded in; every other organization
    /// extends them from there.</summary>
    internal const string PlatformOrganizationSlug = "aetheus";

    internal static readonly IReadOnlyDictionary<string, (string Description, string Category, string[] Resources)> Definitions =
        new Dictionary<string, (string, string, string[])>(StringComparer.Ordinal)
        {
            ["application-light"] = (
                "Reusable differential feedback orchestration delegating project commands to a versioned adapter.",
                "Application Delivery",
                ["application-light-v1.yaml"]),
            ["application-candidate"] = (
                "Reusable build-once candidate orchestration with production baseline, QA and immutable release.",
                "Application Delivery",
                ["application-candidate-v1.yaml", "application-candidate-v2.yaml", "application-candidate-v3.yaml"]),
            ["application-promotion"] = (
                "Reusable promotion orchestration deploying and verifying an already-qualified candidate.",
                "Application Delivery",
                ["application-promotion-v1.yaml"]),
            ["application-ci"] = (
                "Build, test and package the project's immutable application payload: by default restore, build and test the .NET solutions of the repository and keep its source; the project's own CI commands, declared in its pipeline, replace that default.",
                "Application Delivery",
                ["generic/application-ci.yaml"]),
            ["application-quality"] = (
                "Publish and grade coverage, lint and complexity evidence for any language: by default the .NET tests run under dotnet-coverage; the project's own quality commands, declared in its pipeline, replace that default.",
                "Quality",
                ["generic/application-quality.yaml"]),
            ["application-security"] = (
                "Scan secrets, code, dependencies and infrastructure files with the platform's scanners and grade the findings.",
                "Security",
                ["generic/application-security.yaml"]),
            ["application-security-external"] = (
                "Scan code, dependencies and infrastructure files of an external repository with the platform's scanners and grade the findings, with no file required in that repository.",
                "Security",
                ["generic/application-security-external.yaml"]),
            ["application-qa"] = (
                "Validate the immutable application payload: by default build and test the source sealed in it; the project's own QA commands, declared in its pipeline, replace that default.",
                "Quality",
                ["generic/application-qa.yaml"]),
            ["application-qa-extended"] = (
                "Run the project's extended QA commands, declared in its pipeline, the nightly's heavy validation, against the immutable application payload.",
                "Quality",
                ["generic/application-qa-extended.yaml"]),
            ["application-security-history"] = (
                "Scan the repository's whole Git history for secrets and grade it, the nightly extension of the security pipeline.",
                "Security",
                ["generic/application-security-history.yaml"]),
            ["application-nightly"] = (
                "Scheduled complete assurance and drift checks.",
                "Quality",
                ["generic/application-nightly.yaml"]),
            ["host-bluegreen-deploy"] = (
                "Reusable blue-green host cutover built from native step types, with an advisory evidence stage and an automatic rollback.",
                "Application Delivery",
                ["host-bluegreen-deploy-v1.yaml", "host-bluegreen-deploy-v2.yaml"]),
            ["host-bluegreen-revert"] = (
                "Return traffic to the colour a blue-green commit kept in reserve (N-1), in seconds.",
                "Application Delivery",
                ["host-bluegreen-revert-v1.yaml"]),
            ["application-deploy-prod"] = (
                "Restore an immutable candidate and deploy it: by default with Docker Compose on the project's production server; the project's own deployment commands, declared in its pipeline, replace that default.",
                "Application Delivery",
                ["generic/application-deploy-prod.yaml"]),
            ["application-release-fast"] = (
                "Check out and deploy directly, without a candidate: by default with Docker Compose on the project's production server; the project's own fast release commands, declared in its pipeline, replace that default.",
                "Application Delivery",
                ["generic/application-release-fast.yaml"]),
            ["package-telemetry"] = (
                "Build and qualify the project's telemetry package with the commands declared in its pipeline.",
                "Packages",
                ["generic/package-telemetry.yaml"]),
            ["package-web-analytics-dotnet"] = (
                "Build and qualify the project's .NET web analytics package with the commands declared in its pipeline.",
                "Packages",
                ["generic/package-web-analytics-dotnet.yaml"]),
            ["package-web-analytics-browser"] = (
                "Build and qualify the project's browser analytics package with the commands declared in its pipeline.",
                "Packages",
                ["generic/package-web-analytics-browser.yaml"]),
            ["publish-observability-packages"] = (
                "Build the selected package set and run the project's publication commands, declared in its pipeline.",
                "Packages",
                ["generic/publish-observability-packages.yaml"])
        };

    internal static string ReadResource(string fileName)
    {
        var assembly = typeof(DeliveryTemplateCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{ResourceRoot}{fileName}")
            ?? throw new InvalidOperationException(
                $"Embedded delivery pipeline template '{fileName}' is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
