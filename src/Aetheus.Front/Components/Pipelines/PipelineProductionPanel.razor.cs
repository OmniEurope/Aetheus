// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The production deployment card of the setup wizard's summary (ADR-057): the Compose file, the strategy
/// and the server read from the project, and the variables that file asks for with what the creation does for each.</summary>
public partial class PipelineProductionPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupProduction Setup { get; set; } = default!;

    private PipelineProductionAnalysisDto Analysis => Setup.Analysis!;

    // The drop-down validates the expression it is given; the bound state lives in the setup object.
    private string? ComposeFileValue => Setup.ComposeFile;
    private string? ServerValue => Setup.Server;
    private string StrategyValue => Setup.Strategy;

    private IReadOnlyList<OmniOption<string>> ComposeOptions => Analysis.ComposeFiles.ToOptions(item => item, item => item);
    private IReadOnlyList<OmniOption<string>> ServerOptions => Analysis.Servers.ToOptions(item => item.Name, item => item.Name);

    /// <summary>Recette R-77: servers offered to a project that has none are attached when the pipelines are created.</summary>
    private string ServerHelp => Analysis.Servers.Exists(server => server.Candidate)
        ? L["PipelineSetupProdServerCandidateHelp"].Value
        : L["PipelineSetupProdServerHelp"].Value;

    /// <summary>Recette R-89: blue-green first, the strategy proposed by default.</summary>
    private IReadOnlyList<OmniOption<string>> StrategyOptions =>
        new[] { ProductionStrategy.BlueGreen, ProductionStrategy.Replace }
            .ToOptions(item => item, item => item == ProductionStrategy.BlueGreen ? L["PipelineSetupProdBlueGreen"].Value : L["PipelineSetupProdReplace"].Value);

    /// <summary>What is shown: a variable the deployment already provides, or one with a default the Compose file
    /// names, is not the user's concern and is left out.</summary>
    private List<ProductionVariableDto> Listed => Analysis.Variables
        .Where(variable => variable.Kind is not (ProductionVariableKind.Provided or ProductionVariableKind.Optional))
        .ToList();

    private string ConfigRepositoryText => Analysis.ConfigRepository is { } repository
        ? string.Format(L["PipelineSetupProdConfigRepoExisting"], repository.Name)
        : L["PipelineSetupProdConfigRepoNew"].Value;

    // A field binds to a property of an object, which an indexer or a method call is not: one small holder per variable.
    private readonly Dictionary<string, ValueBox> _boxes = [];

    private ValueBox BoxOf(string name) =>
        _boxes.TryGetValue(name, out var box) ? box : _boxes[name] = new ValueBox(Setup, name);

    private sealed class ValueBox(PipelineSetupProduction setup, string name)
    {
        public string Value
        {
            get => setup.Values.GetValueOrDefault(name) ?? string.Empty;
            set => setup.SetValue(name, value ?? string.Empty);
        }
    }

    private Task ChooseComposeFileAsync(string? composeFile) => Setup.ChooseComposeFileAsync(composeFile);

    private void ChooseServer(string? server) => Setup.ChooseServer(server);

    private void ChooseStrategy(string? strategy) => Setup.ChooseStrategy(strategy);

    private static OmniTone Tone(ProductionVariableDto variable) => variable.Kind switch
    {
        ProductionVariableKind.NeedsValue when !variable.Exists => OmniTone.Warning,
        ProductionVariableKind.NeedsValue => OmniTone.Success,
        _ => OmniTone.Neutral
    };

    private string KindText(ProductionVariableDto variable) => variable.Kind switch
    {
        ProductionVariableKind.Port => L["PipelineSetupProdKindPort"],
        ProductionVariableKind.GeneratedSecret => L["PipelineSetupProdKindGenerated"],
        ProductionVariableKind.DefaultValue => L["PipelineSetupProdKindDefault"],
        _ => L["PipelineSetupProdKindNeedsValue"]
    };
}
