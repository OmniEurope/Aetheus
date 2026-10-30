// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.Pages.Servers;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-113: a refused launch reaches the French user in French, from the problem codes the API
/// sends next to its English message, through the application's real resource files.</summary>
public sealed class LaunchProblemTextTests : BunitContext
{
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly IStringLocalizer<AppStrings> _localizer;

    public LaunchProblemTextTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        _localizer = MailOperationDialogValidationTests.RealLocalizer();
    }

    protected override void Dispose(bool disposing)
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.CurrentCulture = _previousCulture;
        base.Dispose(disposing);
    }

    private static ProblemDto Problem(string code, params (string Name, string Value)[] parameters) =>
        new(code, parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value));

    [Fact]
    public void Describe_KnownCode_IsTheFrenchSentenceWithItsParameters()
    {
        var text = LaunchProblemText.Describe(
            [Problem("ConfigTemplateMissingInConfigRepository",
                ("path", ".pipeline/configs/apache/x.conf"), ("steps", "'Apache'"), ("commit", "abc123"))],
            _localizer);

        Assert.Equal(
            "Le modèle de configuration « .pipeline/configs/apache/x.conf » (utilisé par : 'Apache') est introuvable, "
            + "ni dans le dépôt de configuration du projet, ni au commit abc123 de sa source. Avant de lancer, préparez le "
            + "déploiement de production depuis l'assistant de configuration du pipeline : il crée ce modèle.",
            text);
    }

    [Fact]
    public void Describe_ProblemOfATriggeredPipeline_NamesTheChainFirst()
    {
        var text = LaunchProblemText.Describe(
            [Problem("RequiredVaultMissing", ("vault", "prod-secrets"), ("chain", "candidate -> deploy"))], _localizer);

        Assert.Equal(
            "candidate -> deploy : Le coffre « prod-secrets » est requis par ce pipeline et n'existe pas ici. "
            + "Créez-le et ajoutez les secrets que le pipeline lit, puis lancez.",
            text);
    }

    [Fact]
    public void DescribeStored_AWarningStoredAsAProblem_IsTheFrenchSentence()
    {
        var stored = StoredProblem.Compose(
            Problem("VariableLibraryNotFound", ("library", "prod-vars")), "Variable library 'prod-vars' not found.");

        Assert.Equal("Bibliothèque de variables « prod-vars » introuvable.", LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_AWarningThatWrapsARefusal_WritesTheFrenchRefusalWhereTheSentenceNamesTheDetail()
    {
        var stored = StoredProblem.Compose(
            Problem("StepFailedDeploy", ("step", "Deploy prod"), ("detail", "Vault 'x' is missing")),
            "Deploy step 'Deploy prod': Vault 'x' is missing",
            [Problem("RequiredVaultMissing", ("vault", "x"))]);

        Assert.Equal(
            "Pas de déploiement « Deploy prod » : Le coffre « x » est requis par ce pipeline et n'existe pas ici. "
            + "Créez-le et ajoutez les secrets que le pipeline lit, puis lancez.",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    /// <summary>Recette R-23 (W8): a QA run whose project declares no browser end-to-end command says so on the run page,
    /// and the candidate that triggered it says it too, both in French.</summary>
    [Fact]
    public void DescribeStored_TheQaWarningOfNoEndToEndCommand_IsSaidInFrench_AlsoOnTheCandidate()
    {
        const string english = "No browser end-to-end command is declared (APPLICATION_QA_E2E_COMMANDS): the browser tests were not run against the deployed version.";
        const string french = "Aucune commande de tests de bout en bout n'est déclarée (APPLICATION_QA_E2E_COMMANDS) : "
            + "les tests navigateur n'ont pas été exécutés sur la version déployée.";
        var warning = Problem("RunWarning_QaE2eCommandMissing");
        var onQa = StoredProblem.Compose(warning, english);
        var onCandidate = StoredProblem.Compose(
            Problem("ChildRunWarning", ("stage", "QA"), ("run", "12"), ("detail", english)), $"Stage 'QA' (run #12): {english}", [warning]);

        Assert.Equal(french, LaunchProblemText.DescribeStored(onQa, _localizer));
        Assert.Equal($"Étape « QA » (run n°12) : {french}", LaunchProblemText.DescribeStored(onCandidate, _localizer));
    }

    [Fact]
    public void DescribeStored_TheQaWarningOfNoEnvironment_IsSaidInFrench()
    {
        var stored = StoredProblem.Compose(Problem("RunWarning_QaComposeFileEmpty"), "No QA environment was deployed.");

        Assert.Equal(
            "Aucun environnement de QA n'a été déployé (COMPOSE_FILE est vide dans ce pipeline) : rien n'a été validé sur une "
            + "application en marche. Seules les commandes de QA du projet ont été exécutées.",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_AnUnknownCode_ShowsTheEnglishSentenceInsideIt()
    {
        var stored = StoredProblem.Compose(Problem("NoSuchCodeYet"), "A sentence from a newer back.");

        Assert.Equal("A sentence from a newer back.", LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_AWarningStoredBeforeProblemsHadCodes_IsShownAsItIs()
    {
        Assert.Equal(
            "Stage 'Build': no online pipeline runner available in the organization.",
            LaunchProblemText.DescribeStored("Stage 'Build': no online pipeline runner available in the organization.", _localizer));
    }

    [Fact]
    public void Describe_OneUnknownCode_GivesNothing_SoTheEnglishMessageIsShownWhole()
    {
        Assert.Null(LaunchProblemText.Describe(
            [Problem("RequiredDockerMissing"), Problem("NotACodeThisFrontKnows")], _localizer));
        var outcome = new ApiOutcome<PipelineRunDto, YamlValidationResultDto>(
            null, null, false, HttpStatusCode.BadRequest, "This run cannot complete with the current configuration: X.")
        { ErrorProblems = [Problem("NotACodeThisFrontKnows")] };

        Assert.Equal("This run cannot complete with the current configuration: X.", outcome.LocalizedErrorMessage(_localizer));
    }

    [Fact]
    public void Describe_ParameterValues_AreInsertedOnceAndAsTheyAre()
    {
        // A stage name is repository data: it is neither substituted again nor interpreted as markup.
        var text = LaunchProblemText.Describe(
            [Problem("RequiredEnvironmentMissing", ("environment", "<b>{vault}</b>"))], _localizer);

        Assert.Equal("L'environnement « <b>{vault}</b> » est requis par ce pipeline et n'existe pas ici.", text);
    }

    [Fact]
    public async Task TriggerAsync_RefusalWithProblemCodes_ShowsTheFrenchReasonInTheToast()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        var body = JsonSerializer.Serialize(
            new ApiError
            {
                Message = "This run cannot complete with the current configuration: Port 8080 on server 'web' is already registered to 'shop'.",
                Problems =
                [
                    Problem("PortRegisteredToAnother", ("port", "8080"), ("server", "web"), ("owner", "shop"), ("declaredAt", "2026-10-01")),
                    Problem("RequiredDockerMissing"),
                ]
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        handler.SetRawResponse(HttpMethod.Post, "api/pipelines/5/run", body, "application/json", HttpStatusCode.BadRequest);
        var launcher = new PipelineRunLauncher(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<NavigationManager>(),
            Services.GetRequiredService<NotifyHelper>(),
            _localizer);

        var result = await launcher.TriggerAsync(5, sourceBranch: null, parameters: null);

        Assert.Null(result);
        var notification = Assert.Single(Services.Toasts(), message => message.Severity == OmniSeverity.Danger);
        Assert.Equal(
            "Le port 8080 du serveur « web » est déjà réservé par « shop » (déclaré le 2026-10-01). Choisissez un autre "
            + "port ou libérez cette réservation avant de déployer. Aucun exécuteur enrôlé n'indique que Docker est disponible.",
            notification.Detail);
    }
}
