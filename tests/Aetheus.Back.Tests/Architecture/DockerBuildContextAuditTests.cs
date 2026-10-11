// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class DockerBuildContextAuditTests
{
    [Fact]
    public void RootDockerIgnore_ExcludesHostDotnetBuildOutputs()
    {
        var root = FindRepoRoot();
        var rules = File.ReadAllLines(Path.Combine(root, ".dockerignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.Contains("**/bin", rules);
        Assert.Contains("**/obj", rules);
        Assert.DoesNotContain(rules, rule =>
            rule.StartsWith('!')
            && (rule.Contains("/bin", StringComparison.OrdinalIgnoreCase)
                || rule.Contains("/obj", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BackDockerfile_RestoresRidAssetsBeforeCopyingSourceTrees()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.back"));

        // The backend publish moved to the host (deploy/scripts/publish-application.sh). What still
        // compiles in this image is the EF migrations bundle and the two agent releases, and they
        // keep the same ordering requirement: project files restored first, sources copied after, so
        // a source-only change does not invalidate the restore layer.
        var restore = dockerfile.IndexOf(
            "dotnet restore Aetheus.Back/Aetheus.Back.csproj -r linux-x64",
            StringComparison.Ordinal);
        var sourceCopy = dockerfile.IndexOf("COPY src/Aetheus.Back/ Aetheus.Back/", StringComparison.Ordinal);
        var bundle = dockerfile.IndexOf("dotnet-ef migrations bundle", StringComparison.Ordinal);

        Assert.True(restore >= 0, "The backend image must restore linux-x64 assets explicitly.");
        Assert.True(sourceCopy > restore, "Project files must be restored before source-only layers are copied.");
        Assert.True(bundle > sourceCopy, "The EF bundle must be built after the source layer is copied.");
        Assert.DoesNotContain("dotnet publish Aetheus.Back/Aetheus.Back.csproj", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateDockerfiles_ApplyRepositoryBuildPolicy()
    {
        var root = FindRepoRoot();
        var back = File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.back"));
        var front = File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.front"));

        Assert.Contains("COPY Directory.Build.targets .", back, StringComparison.Ordinal);
        // The front image compiles nothing at all now: it copies the host publish. An SDK stage
        // creeping back in would silently reintroduce the second compilation this removed, and would
        // do it with a build context that cannot see the repository-wide build policy.
        Assert.DoesNotContain("dotnet publish", front, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", front, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet/sdk:", front, StringComparison.Ordinal);
        Assert.Contains("COPY .pipeline-publish/frontend/wwwroot ./wwwroot", front, StringComparison.Ordinal);
        Assert.Contains("COPY .pipeline-publish/static-server/ .", front, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPublish_KeepsThePinnedStaticServerContract()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "publish-application.sh"));
        var project = File.ReadAllText(Path.Combine(root, "deploy", "docker", "StaticServer.csproj"));

        // The static server used to be restored and published inside the front image; the contract it
        // had to honour there is unchanged, it simply moved to the script that now produces it.
        Assert.DoesNotContain("dotnet new", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet add package", script, StringComparison.Ordinal);
        // -p:Configuration=Release holds the restore to the TRACKED lock: without a configuration
        // Directory.Build.props sends it to obj/packages..lock.json, a file the run generates itself.
        // DeliveryReproducibilityAuditTests states the same contract from the reproducibility side.
        Assert.Contains("StaticServer.csproj\" -p:Configuration=Release --locked-mode", script, StringComparison.Ordinal);
        // Tied to the static server's own publish, as the Dockerfile line it replaces was: a bare
        // "--no-restore" anywhere in the script would leave "published in Release from the locked
        // restore" unguarded.
        Assert.Contains(
            "-c Release -o \"$OUTPUT/static-server\" --no-restore", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Components.WebAssembly.Server", project, StringComparison.Ordinal);
        Assert.Contains("..\\..\\src\\Aetheus.WebAnalytics\\Aetheus.WebAnalytics.csproj", project, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPublish_RequiresEveryExternalEmbeddedResourceInput()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "deploy", "scripts", "publish-application.sh"));

        // Several of these are declared in the csproj files with a glob, and a glob that matches
        // nothing embeds nothing without failing the build. The Dockerfiles used to make that
        // impossible by COPYing each one into a partial context. Publishing from a full checkout
        // removed that accidental guard, so the script has to state the requirement itself.
        string[] required =
        [
            "scanner-manifest.json",
            ".aetheus/security-rules/opengrep/aetheus-security.yml",
            "deploy/pipelines/toto-conformance-fixture/common",
            // v1 and v2 are separate EmbeddedResource globs of Aetheus.Back, not sub-paths of the
            // common one: each carries the fixture variant a conformance run compares against.
            "deploy/pipelines/toto-conformance-fixture/v1",
            "deploy/pipelines/toto-conformance-fixture/v2",
            "deploy/pipelines/toto-vulnerable-fixture",
            "deploy/pipelines/toto-*.yaml",
            "deploy/pipeline-templates/*.yaml",
            "deploy/pipeline-templates/generic/*.yaml",
            "deploy/scripts/generate-delivery-contract.mjs",
            "deploy/scripts/verify-delivery-promotion.mjs",
            "deploy/scripts/resolve-injected-nuget-packages.mjs",
            "deploy/scripts/generate-artifact-provenance.mjs",
            "deploy/scripts/generate-candidate-assurance-contract.mjs",
            "deploy/scripts/verify-candidate-assurance-contract.mjs",
            "deploy/scripts/zap-active-automation.sh",
            "deploy/scripts/publish-observability-package.sh",
            "deploy/scripts/promote-observability-packages.sh",
            "packages/aetheus-web-analytics/src/aetheus-web-analytics.js"
        ];
        Assert.All(required, path =>
            Assert.Contains($"require_path \"$WORKSPACE/{path}\"", script, StringComparison.Ordinal));
    }

    [Fact]
    public void DockerfilesThatBuildSharedOrAgentCore_CopyExternalEmbeddedResources()
    {
        var root = FindRepoRoot();
        var dockerfiles = new Dictionary<string, string[]>
        {
            ["Dockerfile.agent"] =
            [
                "COPY scanner-manifest.json /scanner-manifest.json",
                "COPY .aetheus/security-rules/opengrep/aetheus-security.yml /.aetheus/security-rules/opengrep/aetheus-security.yml",
                "COPY deploy/scripts/zap-active-automation.sh /deploy/scripts/zap-active-automation.sh",
                "COPY deploy/scripts/publish-observability-package.sh /deploy/scripts/publish-observability-package.sh",
                "COPY deploy/scripts/promote-observability-packages.sh /deploy/scripts/promote-observability-packages.sh"
            ],
            ["Dockerfile.back"] =
            [
                "COPY scanner-manifest.json /scanner-manifest.json",
                "COPY .aetheus/security-rules/opengrep/aetheus-security.yml /.aetheus/security-rules/opengrep/aetheus-security.yml",
                "COPY deploy/scripts/zap-active-automation.sh /deploy/scripts/zap-active-automation.sh",
                "COPY deploy/scripts/publish-observability-package.sh /deploy/scripts/publish-observability-package.sh",
                "COPY deploy/scripts/promote-observability-packages.sh /deploy/scripts/promote-observability-packages.sh"
            ],
            // Dockerfile.front is absent on purpose: it compiles nothing any more, so it builds
            // neither Aetheus.Shared nor Aetheus.Agent.Core and has no embedded resource to carry.
            // The requirement moved to deploy/scripts/publish-application.sh, pinned by
            // HostPublish_RequiresEveryExternalEmbeddedResourceInput above.
            ["Dockerfile.vpssim"] =
            [
                "COPY scanner-manifest.json ./",
                "COPY .aetheus/security-rules/opengrep/aetheus-security.yml .aetheus/security-rules/opengrep/aetheus-security.yml",
                "COPY deploy/scripts/zap-active-automation.sh deploy/scripts/zap-active-automation.sh",
                "COPY deploy/scripts/publish-observability-package.sh deploy/scripts/publish-observability-package.sh",
                "COPY deploy/scripts/promote-observability-packages.sh deploy/scripts/promote-observability-packages.sh"
            ]
        };

        foreach (var (fileName, expectedCopies) in dockerfiles)
        {
            var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "docker", fileName));
            Assert.All(expectedCopies, expectedCopy =>
                Assert.Contains(expectedCopy, dockerfile, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BackDockerfile_AgentBuildStagesCopyAgentCoreExternalEmbeddedResources()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.back"));
        var expectedCopies = new[]
        {
            "COPY deploy/scripts/zap-active-automation.sh /deploy/scripts/zap-active-automation.sh",
            "COPY deploy/scripts/publish-observability-package.sh /deploy/scripts/publish-observability-package.sh",
            "COPY deploy/scripts/promote-observability-packages.sh /deploy/scripts/promote-observability-packages.sh"
        };

        AssertStageCopiesResources("agent-linux-build", "agent-windows-build");
        AssertStageCopiesResources("agent-windows-build", "agent-release");

        void AssertStageCopiesResources(string stageName, string nextStageName)
        {
            var stageStart = dockerfile.IndexOf($"AS {stageName}", StringComparison.Ordinal);
            Assert.True(stageStart >= 0, $"The {stageName} stage must exist.");

            var stageEnd = dockerfile.IndexOf($"AS {nextStageName}", stageStart, StringComparison.Ordinal);
            Assert.True(stageEnd > stageStart, $"The {stageName} stage must end before {nextStageName}.");

            var stage = dockerfile[stageStart..stageEnd];
            Assert.All(expectedCopies, expectedCopy =>
                Assert.Contains(expectedCopy, stage, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BackDockerfile_BackendAndEfStagesCopyBackendExternalEmbeddedResources()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.back"));
        var expectedCopies = new[]
        {
            "COPY deploy/pipelines/toto-conformance-fixture/ /deploy/pipelines/toto-conformance-fixture/",
            "COPY deploy/pipelines/toto-vulnerable-fixture/ /deploy/pipelines/toto-vulnerable-fixture/",
            "COPY deploy/pipelines/toto-*.yaml /deploy/pipelines/",
            "COPY deploy/pipeline-templates/*.yaml /deploy/pipeline-templates/",
            "COPY deploy/pipeline-templates/generic/*.yaml /deploy/pipeline-templates/generic/",
            "COPY deploy/scripts/generate-delivery-contract.mjs /deploy/scripts/generate-delivery-contract.mjs",
            "COPY deploy/scripts/verify-delivery-promotion.mjs /deploy/scripts/verify-delivery-promotion.mjs",
            "COPY deploy/scripts/resolve-injected-nuget-packages.mjs /deploy/scripts/resolve-injected-nuget-packages.mjs"
        };

        // Only ef-bundle now: the backend publish moved to the host, where the whole repository is
        // present and publish-application.sh asserts each of these inputs itself.
        AssertStageCopiesResources("ef-bundle", "runtime");

        void AssertStageCopiesResources(string stageName, string nextStageName)
        {
            var stageStart = dockerfile.IndexOf($"AS {stageName}", StringComparison.Ordinal);
            Assert.True(stageStart >= 0, $"The {stageName} stage must exist.");

            var stageEnd = dockerfile.IndexOf($"AS {nextStageName}", stageStart, StringComparison.Ordinal);
            Assert.True(stageEnd > stageStart, $"The {stageName} stage must end before {nextStageName}.");

            var stage = dockerfile[stageStart..stageEnd];
            Assert.All(expectedCopies, expectedCopy =>
                Assert.Contains(expectedCopy, stage, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void VpsSimDockerfiles_DefaultToNonRootAndNeverUseImplicitSshPublication()
    {
        var root = FindRepoRoot();
        var ignore = File.ReadAllText(Path.Combine(root, ".trivyignore.yaml"));
        Assert.Contains("misconfigurations: []", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("AVD-DS-0002", ignore, StringComparison.Ordinal);

        var simulatorDockerfiles = new[]
        {
            File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.vpssim")),
            File.ReadAllText(Path.Combine(root, "deploy", "docker", "Dockerfile.vpssim-blank"))
        };
        Assert.All(simulatorDockerfiles, dockerfile =>
        {
            Assert.Contains("USER vpssim-runtime", dockerfile, StringComparison.Ordinal);
            Assert.DoesNotContain("EXPOSE 22", dockerfile, StringComparison.Ordinal);
        });

        var simulatorComposeFiles = new[]
        {
            File.ReadAllText(Path.Combine(root, "deploy", "compose", "vpssim.compose.yml")),
            File.ReadAllText(Path.Combine(root, "deploy", "compose", "vpssim-blank.compose.yml"))
        };
        Assert.All(simulatorComposeFiles, compose =>
        {
            Assert.Contains("user: \"0:0\"", compose, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1:${VPSSIM_SSH_PORT:-2222}:22", compose, StringComparison.Ordinal);
        });

        var productionDefinitions = RepositoryScan.Enumerate(Path.Combine(root, "deploy"), "*")
            .Where(path => !Path.GetFileName(path).StartsWith("vpssim", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".yaml" or ".yml" or ".conf" or ".sh")
            .Select(File.ReadAllText)
            .ToList();
        Assert.DoesNotContain(productionDefinitions, definition =>
            definition.Contains("Dockerfile.vpssim", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Dockerfile.agent", "", "Aetheus.Agent.Linux")]
    [InlineData("Dockerfile.vpssim", "src/", "Aetheus.Agent.Linux")]
    public void AgentDockerfiles_CopyEveryProjectTheAgentReferences(
        string dockerfileName, string targetPrefix, string entryProject)
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "docker", dockerfileName));

        // The closure is read from the csproj files, not listed here: Aetheus.Analyzers once went
        // missing from these images because a hand-written list did not follow a new reference
        // (audit STATIC-001). A project absent from the context either breaks the restore or, when
        // only its csproj is present, compiles an empty analyzer and silently drops PRM/SEC rules.
        foreach (var project in ProjectReferenceClosure(root, entryProject))
        {
            Assert.Contains(
                $"COPY src/{project}/{project}.csproj {targetPrefix}{project}/", dockerfile, StringComparison.Ordinal);
            Assert.Contains(
                $"COPY src/{project}/ {targetPrefix}{project}/", dockerfile, StringComparison.Ordinal);
        }
    }

    private static SortedSet<string> ProjectReferenceClosure(string root, string entryProject)
    {
        var closure = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([entryProject]);
        while (pending.TryPop(out var project))
        {
            if (!closure.Add(project))
            {
                continue;
            }

            var csproj = File.ReadAllText(Path.Combine(root, "src", project, $"{project}.csproj"));
            foreach (System.Text.RegularExpressions.Match reference in System.Text.RegularExpressions.Regex.Matches(
                         csproj, @"<ProjectReference\s+Include=""\.\.\\(?<name>[^\\""]+)\\"))
            {
                pending.Push(reference.Groups["name"].Value);
            }
        }

        Assert.Contains("Aetheus.Analyzers", closure);
        return closure;
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
