// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.ExternalRepos;

namespace Aetheus.Front.Components.Projects.ProjectDetailSections;

/// <summary>
/// Recette R-4 (decision of 2026-11-10, option B): says where the project's code is read from. An external address
/// without a project mirror is never read by Aetheus: the notice says so and, for a writer, creates the mirror from
/// that address. A project whose source is a mirror says its code is read from that mirror.
/// </summary>
public partial class ProjectRepositorySourceNotice
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>The address the project names (<c>ProjectDto.RepositoryUrl</c>).</summary>
    [Parameter] public string? RepositoryUrl { get; set; }

    /// <summary>The project has a repository in Aetheus (internal or mirror): its address is then not what is read.</summary>
    [Parameter] public bool HasRepository { get; set; }

    /// <summary>The project's source is the mirror of an external repository.</summary>
    [Parameter] public bool ReadsFromMirror { get; set; }

    [Parameter] public bool CanWrite { get; set; }

    /// <summary>Raised once the mirror is created, so the page reloads the project and its repositories.</summary>
    [Parameter] public EventCallback OnMirrored { get; set; }

    private bool _creating;
    private string? _error;

    private bool AddressUnmirrored => !HasRepository && ExternalRepositoryAddress.IsUnmirrored(RepositoryUrl);

    private async Task CreateMirrorAsync()
    {
        _creating = true;
        _error = null;
        try
        {
            var outcome = await Api.Git.MirrorProjectRepositoryAddressAsync(ProjectId);
            if (outcome.Value is null)
            {
                _error = string.IsNullOrWhiteSpace(outcome.Error?.Message) ? L["OperationFailed"].Value : outcome.Error.Message;
                return;
            }
            Toast.Success("Saved", "ProjectMirrorCreated");
            await OnMirrored.InvokeAsync();
        }
        finally
        {
            _creating = false;
        }
    }
}
