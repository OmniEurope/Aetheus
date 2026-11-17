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
        const string english = "No browser end-to-end command is declared (APPLICATION_QA_E2E_COMMANDS): the browser tests were not run against the deployed version. Set it in this pipeline's variables, or to none when the project has no browser test.";
        const string french = "Aucune commande de tests de bout en bout n'est déclarée (APPLICATION_QA_E2E_COMMANDS) : "
            + "les tests navigateur n'ont pas été exécutés sur la version déployée. "
            + "Renseignez-la dans les variables de ce pipeline, ou à none si le projet n'a pas de test navigateur.";
        var warning = Problem("RunWarning_QaE2eCommandMissing");
        var onQa = StoredProblem.Compose(warning, english);
        var onCandidate = StoredProblem.Compose(
            Problem("ChildRunWarning", ("stage", "QA"), ("run", "12"), ("detail", english)), $"Stage 'QA' (run #12): {english}", [warning]);

        Assert.Equal(french, LaunchProblemText.DescribeStored(onQa, _localizer));
        Assert.Equal($"Étape « QA » (run n°12) : {french}", LaunchProblemText.DescribeStored(onCandidate, _localizer));
    }

    /// <summary>Recette R-67: the nightly's extended QA with no declared command warns instead of failing, and the warning is
    /// said in French, on the run and on the nightly that triggered it.</summary>
    [Fact]
    public void DescribeStored_TheWarningOfNoExtendedQaCommand_IsSaidInFrench_AlsoOnTheNightly()
    {
        const string english = "No extended QA command is declared (APPLICATION_QA_EXTENDED_COMMANDS): nothing was run. Set it in this pipeline's variables, or to none when the project has none.";
        const string french = "Aucune commande de QA étendue n'est déclarée (APPLICATION_QA_EXTENDED_COMMANDS) : rien n'a été exécuté. "
            + "Renseignez-la dans les variables de ce pipeline, ou à none si le projet n'en a pas.";
        var warning = Problem("RunWarning_QaExtendedCommandMissing");
        var onNightly = StoredProblem.Compose(
            Problem("ChildRunWarning", ("stage", "NightlyQAExtended"), ("run", "4"), ("detail", english)),
            $"Stage 'NightlyQAExtended' (run #4): {english}", [warning]);

        Assert.Equal(french, LaunchProblemText.DescribeStored(StoredProblem.Compose(warning, english), _localizer));
        Assert.Equal($"Étape « NightlyQAExtended » (run n°4) : {french}", LaunchProblemText.DescribeStored(onNightly, _localizer));
    }

    /// <summary>R-21: the artifact resolution errors of a restore step are said in French as the cause of the step's
    /// sentence, each code with its parameters.</summary>
    [Theory]
    [InlineData("ArtifactNotFoundAtCommit", "l'artefact « ArtifactManifest-artifacts » est introuvable sur un run réussi de aetheus-ci au commit abc123.")]
    [InlineData("ArtifactNeverProduced", "l'artefact « ArtifactManifest-artifacts » n'a jamais été produit par un run réussi de aetheus-ci.")]
    [InlineData("ArtifactNotFoundOnRun", "l'artefact « ArtifactManifest-artifacts » est introuvable sur le run réussi 42 de aetheus-ci.")]
    [InlineData("ArtifactSelectorInvalid", "« artifact_source_selector » doit valoir « same-commit » ou « latest-successful », et non « oldest ».")]
    [InlineData("ArtifactSourceRequired", "Renseignez « release », ou à la fois « artifact » et « artifact_source_pipeline ».")]
    [InlineData("ArtifactRunNoVerifiedCommit", "le run en cours n'a pas de commit source vérifié.")]
    [InlineData("ArtifactRunNoProject", "le run en cours n'est rattaché à aucun projet.")]
    [InlineData("ArtifactSourceRunOtherProject", "le run source appartient à un autre projet.")]
    [InlineData("ArtifactSourceRunOtherCommit", "le run source appartient à un autre commit source.")]
    [InlineData("ArtifactSourceRunNotSucceeded", "le run source n'a pas réussi : son artefact producteur facultatif n'est pas disponible.")]
    [InlineData("ArtifactReleaseWithSourcePipeline", "« release » ne peut pas être combiné avec « artifact_source_pipeline ».")]
    [InlineData("ArtifactReleaseNotRetained", "la release « 1.2.3 » n'a aucun artefact conservé dans ce projet.")]
    [InlineData("ArtifactTargetDirectoryUnsafe", "« target_directory » doit être un chemin relatif sûr.")]
    [InlineData("ArtifactInvalidSha256", "l'artefact « ArtifactManifest-artifacts » n'a pas de SHA-256 de référence valide.")]
    public void DescribeStored_AnArtifactResolutionCause_IsSaidInFrenchAfterTheRestoreSentence(string code, string french)
    {
        var cause = Problem(code,
            ("artifact", "ArtifactManifest-artifacts"), ("pipeline", "aetheus-ci"), ("commit", "abc123"), ("run", "42"),
            ("release", "1.2.3"), ("selector", "oldest"), ("sameCommit", "same-commit"), ("latestSuccessful", "latest-successful"));
        var stored = StoredProblem.Compose(
            Problem("StepFailedRestoreArtifacts", ("step", "Restore exact CI artifact manifest"), ("detail", "english detail")),
            "Restore-artifacts step 'Restore exact CI artifact manifest': english detail", [cause]);

        Assert.Equal(
            $"Pas de restauration d'artefacts « Restore exact CI artifact manifest » : {french}",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_AnOldRestoreWarningStoredWithPlainEnglish_StaysAsStored()
    {
        const string old = "Restore-artifacts step 'x': artifact 'a' was not found on a successful aetheus-ci run at commit abc.";

        Assert.Equal(old, LaunchProblemText.DescribeStored(old, _localizer));
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

    private static string DroppedOccurrences(string code, params (string Name, string Value)[] parameters) =>
        StoredProblem.Compose(
            Problem("LaunchRefused", ("source", "Scheduler"), ("detail", "English fallback")),
            "The automated launch (Scheduler) was refused: English fallback",
            [Problem(code, parameters)]);

    [Fact]
    public void DescribeStored_OneDroppedScheduledOccurrence_IsSaidInFrenchInTheSingular()
    {
        var stored = DroppedOccurrences(
            "ScheduleOccurrenceMissed", ("count", "1"), ("first", "2026-11-06 02:00"), ("last", "2026-11-06 02:00"), ("limit", "2"));

        Assert.Equal(
            "Le lancement automatique (planificateur) a été refusé : L'occurrence planifiée du 2026-11-06 02:00 UTC a été manquée "
            + "au-delà de la limite de rattrapage de 2 h et n'a pas été lancée.",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_SeveralDroppedScheduledOccurrences_AreSaidInFrenchInThePlural()
    {
        var stored = DroppedOccurrences(
            "ScheduleOccurrencesMissed", ("count", "3"), ("first", "2026-11-06 02:00"), ("last", "2026-11-06 04:00"), ("limit", "2"));

        Assert.Equal(
            "Le lancement automatique (planificateur) a été refusé : 3 occurrences planifiées, du 2026-11-06 02:00 au 2026-11-06 04:00 UTC, "
            + "ont été manquées au-delà de la limite de rattrapage de 2 h et n'ont pas été lancées.",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_DroppedScheduledOccurrences_AreSaidInEnglishUnderTheEnglishCulture()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var stored = DroppedOccurrences(
            "ScheduleOccurrencesMissed", ("count", "3"), ("first", "2026-11-06 02:00"), ("last", "2026-11-06 04:00"), ("limit", "2"));

        Assert.Equal(
            "The automated launch (scheduler) was refused: 3 scheduled occurrences, from 2026-11-06 02:00 to 2026-11-06 04:00 UTC, "
            + "were missed for longer than the catch-up limit of 2 h and were not run.",
            LaunchProblemText.DescribeStored(stored, _localizer));
    }

    [Fact]
    public void DescribeStored_ADroppedOccurrenceStoredBeforeTheCode_StillShowsItsEnglishSentence()
    {
        var old = StoredProblem.Compose(
            Problem("LaunchRefused", ("source", "Scheduler"), ("detail", "1 scheduled occurrence(s) from a to b UTC were missed.")),
            "The automated launch (Scheduler) was refused: 1 scheduled occurrence(s) from a to b UTC were missed.");

        Assert.Equal(
            "Le lancement automatique (planificateur) a été refusé : 1 scheduled occurrence(s) from a to b UTC were missed.",
            LaunchProblemText.DescribeStored(old, _localizer));
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

    [Theory]
    [InlineData("Webhook", "webhook")]
    [InlineData("on_success:ci#12", "on_success:ci#12")]
    public void Describe_TheSourceOfARefusal_IsAFrenchWordWhenTheBackNamesItWithOne_ElseTheDataAsItIs(string source, string said)
    {
        var text = LaunchProblemText.Describe(
            [Problem("LaunchRefused", ("source", source), ("detail", "verrou"))], _localizer);

        Assert.Equal($"Le lancement automatique ({said}) a été refusé : verrou", text);
    }

    /// <summary>R-36: the refused run of a follow-up (run 3119 of oe1) says in French which follow-up trigger refused it
    /// and why, with no English text.</summary>
    [Fact]
    public void R36_DescribeStored_ARefusedFollowUp_IsFrenchWithTheReasonInsideIt()
    {
        var stored = StoredProblem.Compose(
            Problem("LaunchRefused", ("source", "on_success:oe1-candidate#3119"), ("detail", "x")),
            "The automated launch (on_success:oe1-candidate#3119) was refused: x",
            [Problem("ChainedDeployServerMissing", ("parameter", "deployServer"), ("pipeline", "oe1-deploy-prod"), ("servers", "2"))]);

        var text = LaunchProblemText.DescribeStored(stored, _localizer);

        Assert.StartsWith("Le lancement automatique (on_success:oe1-candidate#3119) a été refusé : Le paramètre deployServer n'a ni valeur ni défaut", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The automated", text, StringComparison.Ordinal);
    }

    [Fact]
    public void R36_DescribeStored_ARefusedManualLaunch_IsFrenchWithTheReasonInsideIt()
    {
        var stored = StoredProblem.Compose(
            Problem("ManualLaunchRefused", ("detail", "Parameter 'candidateVersion' is required.")),
            "The manual launch was refused: Parameter 'candidateVersion' is required.",
            [Problem("ParameterRequired", ("parameter", "candidateVersion"))]);

        Assert.Equal(
            "Le lancement manuel a été refusé : Le paramètre « candidateVersion » est obligatoire.",
            LaunchProblemText.DescribeStored(stored, _localizer));
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
