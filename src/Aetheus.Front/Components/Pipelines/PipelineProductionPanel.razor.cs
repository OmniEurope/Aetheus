// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The production deployment card of the setup wizard's summary (ADR-057): the Compose file, the strategy
/// and the server read from the project, and the variables that file asks for with what the creation does for each.</summary>
public partial class PipelineProductionPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupProduction Setup { get; set; } = default!;

    private PipelineProductionAnalysisDto Analysis => Setup.Analysis!;

    private Task ReadAgainAsync() => Setup.ReloadAsync();

    /// <summary>R-36: with several servers to choose from, the server is a required field above the card, whatever the repository
    /// gave. A single server is the choice (the card names it).</summary>
    private bool ServerRequiredShown => Setup.ServerRequired && Setup.Analysis is { Servers.Count: > 1 };

    // The drop-down validates the expression it is given; the bound state lives in the setup object.
    private string? ComposeFileValue => Setup.ComposeFile;
    private string? ServerValue => Setup.Server;

    private IReadOnlyList<OmniOption<string>> ComposeOptions => Analysis.ComposeFiles.ToOptions(item => item, item => item);
    private IReadOnlyList<OmniOption<string>> ServerOptions => Analysis.Servers.ToOptions(item => item.Name, item => item.Name);

    /// <summary>Recette R-77: servers offered to a project that has none are attached when the pipelines are created.</summary>
    private string ServerHelp => Analysis.Servers.Exists(server => server.Candidate)
        ? L["PipelineSetupProdServerCandidateHelp"].Value
        : L["PipelineSetupProdServerHelp"].Value;

    /// <summary>Recette R-19: the strategy is chosen on the Capabilities step; the summary only says which one will be applied.</summary>
    private string StrategyLabel => Setup.Strategy == ProductionStrategy.BlueGreen ? L["PipelineSetupProdBlueGreen"].Value : L["PipelineSetupProdReplace"].Value;

    /// <summary>What the user has to type: a variable only the operator knows and the project does not hold yet.</summary>
    private List<ProductionVariableDto> ToFill => Analysis.Variables
        .Where(variable => variable.Kind == ProductionVariableKind.NeedsValue && !variable.Exists)
        .ToList();

    /// <summary>R-40 (a): what is shown folded away. A variable the deployment already provides, or one with a default the
    /// Compose file names, is not the user's concern and is left out.</summary>
    private List<ProductionVariableDto> Generated => Analysis.Variables
        .Where(variable => variable.Kind is not (ProductionVariableKind.Provided or ProductionVariableKind.Optional)
            && !(variable.Kind == ProductionVariableKind.NeedsValue && !variable.Exists))
        .ToList();

    /// <summary>The names of the values the panel asks for, so the summary does not ask for them a second time (R-40 d).</summary>
    public static IReadOnlySet<string> ToFillNames(PipelineProductionAnalysisDto? analysis) =>
        analysis is { Readable: true, ComposeFiles.Count: > 0 }
            ? analysis.Variables.Where(variable => variable.Kind == ProductionVariableKind.NeedsValue)
                .Select(variable => variable.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

    /// <summary>R-54: the state follows the value, never a gesture. A value in the setup (typed, changed or left with the
    /// focus, Enter or not) is shown as typed and is sent by the creation; an emptied field is to fill in again.</summary>
    private bool IsCommitted(string name) => !string.IsNullOrEmpty(Setup.Values.GetValueOrDefault(name));

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
        ProductionVariableKind.NeedsValue when variable.Exists => L["PipelineSetupProdExists"],
        _ => L["PipelineSetupProdKindNeedsValue"]
    };
}
