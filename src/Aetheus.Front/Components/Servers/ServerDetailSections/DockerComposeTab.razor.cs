// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The Docker compose tab: the stack grid, the stack actions, and the inline compose-file editor.
///
/// Unlike the read-only tabs it has an inbound channel: asking for a stack's compose file queues an
/// agent task whose output comes back over SignalR, on the parent's connection. The parent therefore
/// holds an <c>@ref</c> to this component and forwards raw task output to
/// <see cref="HandleTaskOutput"/>, which decides for itself whether the output is one it was waiting
/// for. The editor state stays here so the parent has nothing to reset - <c>@key="ServerId"</c> does it.
/// </summary>
public partial class DockerComposeTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>BACK-012: the backend takes Server.Admin for this tab's agent-reaching writes (compose
    /// deploy, image build and delete, container shell); without it they are not offered.</summary>
    [Parameter] public bool CanAdmin { get; set; }
    [Parameter, EditorRequired] public IReadOnlyList<DockerComposeStackDto> Stacks { get; set; } = [];

    private string _composeSearch = string.Empty;

    // Recette R-227: each heartbeat brings a new stack list; stacks that were not there read bold.
    private OmniDataGrid<DockerComposeStackDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Stacks);

    private bool _composeEditorVisible;
    private string _composeEditorStack = string.Empty;
    private string _composeEditorContent = string.Empty;
    private bool _composeDeploying;
    private bool _composeFileLoading;

    private List<DockerComposeStackDto> FilteredCompose => Stacks
        .Where(s => string.IsNullOrWhiteSpace(_composeSearch)
            || s.Name.Contains(_composeSearch, StringComparison.OrdinalIgnoreCase)
            || s.Status.Contains(_composeSearch, StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>
    /// Consumes one completed agent task forwarded by the parent. Returns without touching anything
    /// when the output is not the compose file this tab is waiting for.
    /// FRONT-017: only the task of the stack open in the editor, and only while its file is still being
    /// requested. A late answer for another stack (open A, then B, A answers last) would otherwise show
    /// A's file under B's title and Deploy would apply it to B; an answer arriving once the file is
    /// shown would overwrite the user's edits.
    /// </summary>
    public void HandleTaskOutput(string taskName, string output)
    {
        if (!_composeEditorVisible || !_composeFileLoading
            || !string.Equals(taskName, DockerTaskNames.ComposeFile(_composeEditorStack), StringComparison.Ordinal))
            return;
        _composeEditorContent = output;
        _composeFileLoading = false;

        // The parent calls this from the SignalR callback, outside the render loop, so the repaint has
        // to be marshalled back onto the renderer's sync context - as the parent does for its own state.
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task ComposeActionAsync(string stackName, DockerComposeAction action)
    {
        var request = new DockerComposeActionRequest { StackName = stackName, Action = action };
        var success = await Api.ServerTools.ExecuteComposeActionAsync(ServerId, request);
        if (success)
        {
            Toast.Success("DockerCompose", "DockerComposeQueued", action, stackName);
        }
        else
        {
            Toast.Error("DockerCompose", "DockerComposeFailed", action, stackName);
        }
    }

    private async Task OpenComposeEditorAsync(string stackName)
    {
        _composeEditorStack = stackName;
        _composeEditorContent = string.Empty;
        _composeEditorVisible = true;
        _composeFileLoading = true;
        var success = await Api.ServerTools.GetComposeFileAsync(ServerId, stackName);
        if (!success)
        {
            Toast.Error("DockerCompose", "DockerComposeFileFailed");
            _composeFileLoading = false;
        }
    }

    private void CloseComposeEditor()
    {
        _composeEditorVisible = false;
        _composeEditorStack = string.Empty;
        _composeEditorContent = string.Empty;
    }

    private async Task SaveComposeFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_composeEditorContent)) return;
        _composeDeploying = true;
        var request = new DockerComposeFileSaveRequest
        {
            StackName = _composeEditorStack,
            Content = _composeEditorContent
        };
        var success = await Api.ServerTools.SaveComposeFileAsync(ServerId, request);
        if (success)
        {
            Toast.Success("DockerCompose", "DockerComposeDeployQueued", _composeEditorStack);
            CloseComposeEditor();
        }
        else
        {
            Toast.Error("DockerCompose", "DockerComposeDeployFailed");
        }
        _composeDeploying = false;
    }

    /// <summary>Recette R2-124: the export writes the running and total services as the cell does.</summary>
    private static readonly Func<DockerComposeStackDto, object?> ServicesExport = stack => $"{stack.RunningCount} / {stack.TotalCount}";
}
