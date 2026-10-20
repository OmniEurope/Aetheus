// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Each refusal of the step validators (AI, DAST API scanner, artifacts, Apache config, inline
/// shell) and of the isolation block, driven through the stage validation a saved definition goes
/// through: the message names the step and the rule, and a well-formed step produces none.</summary>
public sealed class PipelineStepDefinitionBranchTests
{
    private const string Digest = "registry.example/tool@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static PipelineStepDefinition AiStep(Func<PipelineStepDefinition, PipelineStepDefinition>? tweak = null)
    {
        var step = new PipelineStepDefinition { Name = "review", Type = "ai", Profile = "claude", Prompt = "Review." };
        return tweak is null ? step : tweak(step);
    }

    private static (List<string> Errors, List<string> Warnings) Validate(
        PipelineStepDefinition step, Func<PipelineStageDefinition, PipelineStageDefinition>? stageTweak = null)
    {
        var stage = new PipelineStageDefinition { Name = "Build", Agent = "runner-1", Steps = [step] };
        if (stageTweak is not null) stage = stageTweak(stage);
        List<string> errors = [];
        List<string> warnings = [];
        PipelineDefinitionValidator.ValidateStages(
            new PipelineYamlDefinition { Name = "ci", Stages = [stage] }, errors, warnings);
        return (errors, warnings);
    }

    [Fact]
    public void AWellFormedAiStep_IsValid()
    {
        var (errors, _) = Validate(AiStep());

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void AnAiStepWithoutAProfile_IsValid_BecauseTheRunPicksAnAvailableOne(string? profile)
    {
        var (errors, _) = Validate(AiStep(s => s with { Profile = profile }));

        Assert.Empty(errors);
    }

    public static TheoryData<string, string> AiStepRefusals => new()
    {
        { "neither", "AI step 'review' in stage 'Build' must set exactly one of prompt or prompt_file." },
        { "both", "AI step 'review' in stage 'Build' must set exactly one of prompt or prompt_file." },
        { "unsafe-file", "AI step 'review' in stage 'Build' has an unsafe prompt_file." },
        { "context", "AI step 'review' in stage 'Build' has invalid context_paths." },
        { "range", "AI step 'review' in stage 'Build' has an empty or oversized git_range." },
        { "placeholder", "AI step 'review' in stage 'Build': findings_placeholder needs findings: true." },
        { "advisory-gate", "AI step 'review' in stage 'Build' cannot be both advisory and a gate: an advisory verdict blocks nothing." },
    };

    [Theory]
    [MemberData(nameof(AiStepRefusals))]
    public void AnAiStep_BreakingARule_IsRefusedWithItsMessage(string rule, string expected)
    {
        var step = rule switch
        {
            "neither" => AiStep(s => s with { Prompt = null }),
            "both" => AiStep(s => s with { PromptFile = "prompts/review.md" }),
            "unsafe-file" => AiStep(s => s with { Prompt = null, PromptFile = "../outside.md" }),
            "context" => AiStep(s => s with { ContextPaths = ["../secret"] }),
            "range" => AiStep(s => s with { GitRange = new string('a', 5000) }),
            "placeholder" => AiStep(s => s with { FindingsPlaceholder = true }),
            "advisory-gate" => AiStep(s => s with { Advisory = true, Gate = true }),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

        var (errors, _) = Validate(step);

        Assert.Contains(expected, errors);
    }

    private static PipelineStepDefinition ApiScanner(Func<PipelineStepDefinition, PipelineStepDefinition>? tweak = null)
    {
        var step = new PipelineStepDefinition
        {
            Name = "zap",
            Type = "scanner",
            Scanner = "zap-api",
            TargetUrl = "https://staging.example",
            ApiSpecificationUrl = "https://staging.example/openapi.json",
            ApiSpecificationFormat = "openapi",
        };
        return tweak is null ? step : tweak(step);
    }

    private static (List<string> Errors, List<string> Warnings) ValidateInEnvironment(PipelineStepDefinition step) =>
        Validate(step, stage => stage with { Environment = "staging" });

    [Fact]
    public void AWellFormedApiScanner_IsValid()
    {
        var (errors, _) = ValidateInEnvironment(ApiScanner());

        Assert.Empty(errors);
    }

    [Fact]
    public void AnApiScannerWithAListOfSpecifications_IsValid()
    {
        var step = ApiScanner(s => s with
        {
            ApiSpecificationUrl = null,
            ApiSpecificationUrls = ["https://staging.example/a.json", "$(SPEC_URL)"],
            ApiSpecificationFormat = "GraphQL",
        });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnApiScanner_WithOneSpecificationAndAList_IsRefused()
    {
        var step = ApiScanner(s => s with { ApiSpecificationUrls = ["https://staging.example/a.json"] });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' sets both api_specification_url and api_specification_urls; keep one.", errors);
    }

    [Fact]
    public void AnApiScanner_WithTooManySpecifications_IsRefused()
    {
        var urls = Enumerable.Range(0, PipelineStepAnalysisValidator.MaxApiSpecificationUrls + 1)
            .Select(index => $"https://staging.example/{index}.json").ToList();
        var step = ApiScanner(s => s with { ApiSpecificationUrl = null, ApiSpecificationUrls = urls });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' lists more than 16 api_specification_urls.", errors);
    }

    [Fact]
    public void AnApiScanner_WithANonHttpsSpecificationInTheList_IsRefused()
    {
        var step = ApiScanner(s => s with
        {
            ApiSpecificationUrl = null,
            ApiSpecificationUrls = ["http://staging.example/a.json"],
        });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' requires every api_specification_urls entry to be an HTTPS URL.", errors);
    }

    [Fact]
    public void AnApiScanner_WithANonHttpsSpecification_IsRefused()
    {
        var step = ApiScanner(s => s with { ApiSpecificationUrl = "http://staging.example/openapi.json" });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' requires an HTTPS api_specification_url.", errors);
    }

    [Fact]
    public void AnApiScanner_WithAnUnknownFormat_IsRefused()
    {
        var step = ApiScanner(s => s with { ApiSpecificationFormat = "wsdl" });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' api_specification_format must be openapi or graphql.", errors);
    }

    [Fact]
    public void ASpecificationOnAnotherDastScanner_IsRefused()
    {
        var step = ApiScanner(s => s with { Scanner = "zap-passive" });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' API specification fields are reserved for zap-api.", errors);
    }

    [Fact]
    public void DastFieldsOnANonDastScanner_AreRefused()
    {
        var step = ApiScanner(s => s with { Scanner = "gitleaks" });

        var (errors, _) = ValidateInEnvironment(step);

        Assert.Contains("Step 'zap' in stage 'Build' DAST target and API specification fields are reserved for DAST scanners.", errors);
    }

    [Fact]
    public void ADastScanner_WithoutEnvironment_TargetOrWithSelfDeclaredClassification_IsRefused()
    {
        var step = ApiScanner(s => s with { TargetUrl = "ftp://staging.example", TargetClassification = "trusted", Active = true });

        var (errors, _) = Validate(step);

        Assert.Contains("Step 'zap' in stage 'Build' requires an absolute HTTP(S) target_url.", errors);
        Assert.Contains("Step 'zap' in stage 'Build' requires a named Aetheus environment for the trusted DAST policy.", errors);
        Assert.Contains("Step 'zap' in stage 'Build' must not self-declare target_classification; configure DAST on the Aetheus environment.", errors);
        Assert.Contains("Step 'zap' in stage 'Build' active DAST requires the zap-active scanner.", errors);
    }

    [Theory]
    [InlineData("cat <<EOF", true)]
    [InlineData("cat <<-EOF", true)]
    [InlineData("cat <<'EOF'", true)]
    [InlineData("cat <<-\"EOF\"", true)]
    [InlineData("cat <<_END", true)]
    [InlineData("echo 1 << 2 && cat <<EOF", true)]
    [InlineData("echo $((1 << 2))", false)]
    [InlineData("echo 1 <<", false)]
    [InlineData("echo 1 <<-", false)]
    [InlineData("echo 1 <<'", false)]
    [InlineData("echo hello", false)]
    public void AnInlineShell_WarnsOnlyWhenItEmbedsAHeredoc(string shell, bool warns)
    {
        var (_, warnings) = Validate(new PipelineStepDefinition { Name = "inline", Shell = shell });

        Assert.Equal(warns, warnings.Any(warning => warning.Contains("embeds a heredoc", StringComparison.Ordinal)));
    }

    [Fact]
    public void AnInlineShell_OverTheLineBudget_WarnsItBelongsInAScript()
    {
        var shell = string.Join('\n', Enumerable.Repeat("echo step", 31));

        var (_, warnings) = Validate(new PipelineStepDefinition { Name = "long", Shell = shell });

        Assert.Contains(warnings, warning => warning.Contains("inlines 31 lines of shell", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApacheConfigStep_WithoutFiles_IsRefused()
    {
        var (errors, _) = Validate(new PipelineStepDefinition { Name = "site", Type = "apache-config" });

        Assert.Contains("Step 'site' in stage 'Build' requires at least one config_files entry.", errors);
    }

    [Fact]
    public void AnApacheConfigStep_WithAnUnsafeDestinationAndTemplate_IsRefused()
    {
        var step = new PipelineStepDefinition
        {
            Name = "site",
            Type = "apache-config",
            ConfigFiles = new Dictionary<string, string> { ["../evil.conf"] = "../outside.tmpl" },
        };

        var (errors, _) = Validate(step);

        Assert.Contains("Step 'site' in stage 'Build' has an invalid Apache destination '../evil.conf'.", errors);
        Assert.Contains("Step 'site' in stage 'Build' template '../outside.tmpl' must be under .pipeline/configs/.", errors);
    }

    [Fact]
    public void AnApacheConfigStep_WithASafeDestinationAndTemplate_IsValid()
    {
        var step = new PipelineStepDefinition
        {
            Name = "site",
            Type = "apache-config",
            ConfigFiles = new Dictionary<string, string> { ["aetheus.conf"] = ".pipeline/configs/aetheus.conf.tmpl" },
        };

        var (errors, _) = Validate(step);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnArtifactsStep_WithoutNameOrFiles_IsRefused()
    {
        var (errors, _) = Validate(new PipelineStepDefinition { Name = "collect", Type = "artifacts" });

        Assert.Contains("Step 'collect' in stage 'Build' requires artifact_name.", errors);
        Assert.Contains("Step 'collect' in stage 'Build' requires target_files, the files the artifact holds.", errors);
    }

    [Fact]
    public void AnArtifactsStep_InAContainerIsolatedMatrixStage_IsRefused()
    {
        var step = new PipelineStepDefinition
        {
            Name = "collect",
            Type = "artifacts",
            ArtifactName = "bundle",
            TargetFiles = ["out/app.zip"],
        };

        var (errors, _) = Validate(step, stage => stage with
        {
            Isolation = new PipelineIsolationDefinition { Mode = PipelineIsolationDefinition.ModeContainer, Image = Digest },
            Matrix = new Dictionary<string, List<string>> { ["os"] = ["linux", "windows"] },
        });

        Assert.Contains("Step 'collect' in stage 'Build' cannot collect artifacts in a container-isolated stage; declare them on the stage.", errors);
        Assert.Contains("Step 'collect' in stage 'Build' cannot collect artifacts in a matrix: each leg would replace the previous one.", errors);
    }

    [Fact]
    public void AnArtifactsStep_WithNameAndFiles_IsValid()
    {
        var step = new PipelineStepDefinition
        {
            Name = "collect",
            Type = "artifacts",
            ArtifactName = "bundle",
            TargetFiles = ["out/app.zip"],
        };

        var (errors, _) = Validate(step);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnArtifactNameOnAnotherStepType_IsRefused()
    {
        var (errors, _) = Validate(new PipelineStepDefinition { Name = "build", Shell = "dotnet build", ArtifactName = "bundle" });

        Assert.Contains("Step 'build' in stage 'Build' artifact_name is only valid on an artifacts step.", errors);
    }

    [Fact]
    public void IsolationDefinition_NoneOrProcess_HasNoError()
    {
        Assert.Null(PipelineIsolationHelpers.ValidateDefinition("Run", null));
        Assert.Null(PipelineIsolationHelpers.ValidateDefinition("Run", new PipelineIsolationDefinition()));
    }

    [Fact]
    public void IsolationDefinition_ContainerWithAPinnedImage_HasNoError()
    {
        var isolation = new PipelineIsolationDefinition { Mode = PipelineIsolationDefinition.ModeContainer, Image = Digest, Shell = "bash" };

        Assert.Null(PipelineIsolationHelpers.ValidateDefinition("Run", isolation));
    }

    [Fact]
    public void IsolationDefinition_ContainerWithAToolchain_HasNoError()
    {
        var isolation = new PipelineIsolationDefinition { Mode = PipelineIsolationDefinition.ModeContainer, Toolchain = "dotnet" };

        Assert.Null(PipelineIsolationHelpers.ValidateDefinition("Stage 'Build'", isolation));
    }

    public static TheoryData<string, string> IsolationRefusals => new()
    {
        { "mode", "Run: isolation mode 'vm' is invalid." },
        { "neither", "Run: container isolation requires exactly one of 'image' or 'toolchain'." },
        { "both", "Run: container isolation requires exactly one of 'image' or 'toolchain'." },
        { "mutable", "Run: container image must end in an immutable '@sha256:<digest>' reference." },
        { "toolchain-name", "Run: toolchain key 'Not A Name' is invalid." },
        { "shell", "Run: container shell 'zsh' is unsupported." },
        { "toolchain-shell", "Run: a toolchain-backed container takes its shell from the lock manifest." },
    };

    [Theory]
    [MemberData(nameof(IsolationRefusals))]
    public void IsolationDefinition_BreakingARule_ReturnsItsMessage(string rule, string expected)
    {
        var container = new PipelineIsolationDefinition { Mode = PipelineIsolationDefinition.ModeContainer };
        var isolation = rule switch
        {
            "mode" => new PipelineIsolationDefinition { Mode = "vm" },
            "neither" => container,
            "both" => container with { Image = Digest, Toolchain = "dotnet" },
            "mutable" => container with { Image = "registry.example/tool:latest" },
            "toolchain-name" => container with { Toolchain = "Not A Name" },
            "shell" => container with { Image = Digest, Shell = "zsh" },
            "toolchain-shell" => container with { Toolchain = "dotnet", Shell = "bash" },
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

        Assert.Equal(expected, PipelineIsolationHelpers.ValidateDefinition("Run", isolation));
    }
}
