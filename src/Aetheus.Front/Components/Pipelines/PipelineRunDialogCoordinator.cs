// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Coordinates what a pipeline launch needs to know: the branch and the queue parameters.</summary>
public sealed class PipelineRunDialogCoordinator(
    ApiClient api,
    OmniDialogService dialog,
    IStringLocalizer<AppStrings> localizer)
{
    /// <summary>
    /// Opens the single "run with options" dialog: branch and parameters together, both pre-filled.
    /// Returns <c>null</c> when the user cancels, so the caller must not launch.
    /// </summary>
    public async Task<PipelineLaunchChoice?> ConfigureLaunchAsync(int pipelineId)
    {
        var branches = new List<GitLightBranchDto>();
        string? selectedBranch = null;

        var source = await api.Pipelines.GetPipelineSourceAsync(pipelineId);
        if (source is { RepositoryId: > 0 })
        {
            var repositoryTask = api.Git.GetGitRepoAsync(source.RepositoryId);
            var branchesTask = api.Git.GetGitBranchesAsync(source.RepositoryId);
            await Task.WhenAll(repositoryTask, branchesTask).ConfigureAwait(false);

            var repository = await repositoryTask.ConfigureAwait(false);
            branches = OrderBranches(await branchesTask.ConfigureAwait(false), repository?.DefaultBranch);
            if (branches.Count > 0)
            {
                selectedBranch = branches.FirstOrDefault(branch => branch.IsDefault)?.Name
                    ?? branches.FirstOrDefault(branch => string.Equals(
                        branch.Name,
                        repository?.DefaultBranch,
                        StringComparison.Ordinal))?.Name
                    ?? branches.FirstOrDefault(branch => string.Equals(
                        branch.Name,
                        source.Branch,
                        StringComparison.Ordinal))?.Name
                    ?? branches[0].Name;
            }
        }

        List<PipelineRunParameterDto> declared;
        try { declared = await api.Pipelines.GetPipelineRunParametersAsync(pipelineId, selectedBranch).ConfigureAwait(false); }
        catch (Exception exception) when (IsTransportFailure(exception)) { return null; }

        var (releases, releaseTotal) = await LoadReleasesIfAParameterTakesOneAsync(pipelineId, declared);
        var runToEndAllowed = await IsRunToEndAllowedAsync().ConfigureAwait(false);
        return await dialog.OpenAsync<PipelineLaunchDialog>(
            localizer["RunWithOptions"].Value,
            new Dictionary<string, object?>
            {
                { "Branches", branches },
                { "Parameters", declared },
                { "SourceBranch", selectedBranch },
                { "AvailableReleases", releases },
                { "AvailableReleaseTotal", releaseTotal },
                { "ReloadParameters", ReadParametersOrNull(pipelineId) },
                { "RunToEndAllowed", runToEndAllowed }
            },
            new OmniDialogOptions { Width = DialogWidth(declared), AutoFocusFirstElement = false }) as PipelineLaunchChoice;
    }

    /// <summary>Whether the platform accepts "run to end" launches. An unreadable answer (an older
    /// backend without the endpoint included) hides the option: the backend refuses it anyway.</summary>
    private async Task<bool> IsRunToEndAllowedAsync()
    {
        try
        {
            return (await api.Pipelines.GetRunOptionsAsync().ConfigureAwait(false))?.RunToEndAllowed == true;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Hands the dialog a way to re-read the declaration when the user picks another branch, since
    /// a branch's YAML may declare a different <c>parameters:</c> block. Returns <c>null</c> on a
    /// failed read so the dialog keeps the fields it already shows instead of emptying the form.
    /// </summary>
    private Func<string?, Task<List<PipelineRunParameterDto>?>> ReadParametersOrNull(int pipelineId) =>
        async branch =>
        {
            try
            {
                return await api.Pipelines.GetPipelineRunParametersAsync(pipelineId, branch).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                return null;
            }
        };

    /// <summary>Load restorable releases only when a declared parameter can receive one so
    /// unrelated pipelines avoid the request; respect the API page ceiling and report when the
    /// retained list exceeds it.</summary>
    private async Task<(List<ReleaseDto>? Releases, int Total)> LoadReleasesIfAParameterTakesOneAsync(
        int pipelineId, IEnumerable<PipelineRunParameterDto> declared)
    {
        if (RunParameterTargets.ReleaseFillTarget(declared) is null) return (null, 0);
        try
        {
            var pipeline = await api.Pipelines.GetPipelineAsync(pipelineId).ConfigureAwait(false);
            if (pipeline?.ProjectId is not { } projectId) return ([], 0);
            var page = await api.Projects.GetReleasesAsync(1, PaginationDefaults.MaximumPageSize, projectId, deployableOnly: true)
                .ConfigureAwait(false);
            return (page?.Items ?? [], page?.TotalCount ?? 0);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // Purely a picking aid: failing to load it must never block a launch.
            return ([], 0);
        }
    }

    /// <summary>D38: 720 px when the release section is shown (a parameter takes a version, even if no
    /// release can be restored yet: the grid says so), the former 560 px otherwise.</summary>
    internal static string DialogWidth(IEnumerable<PipelineRunParameterDto> declared) =>
        RunParameterTargets.ReleaseFillTarget(declared) is not null ? "720px" : "560px";

    /// <summary>
    /// Everything a read of this API can throw short of a bug. <see cref="HttpRequestException"/>
    /// alone is not enough: the front's handlers add resilience (timeout rejection, open circuit)
    /// and a proxy answering HTML with a 200 surfaces as a <see cref="JsonException"/>. Letting any
    /// of those escape would make a decorative load take down the launch dialog itself.
    /// </summary>
    private static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException
            or JsonException
            or OperationCanceledException
            or TimeoutRejectedException
            or BrokenCircuitException;

    internal static List<Aetheus.Shared.Components.Git.GitLightBranchDto> OrderBranches(
        IEnumerable<Aetheus.Shared.Components.Git.GitLightBranchDto> branches,
        string? defaultBranch) =>
        branches
            .OrderBy(branch =>
                branch.IsDefault || string.Equals(branch.Name, defaultBranch, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What a plain "Run" needs: the declared parameters taken at their defaults, with no dialog.
    ///
    /// A dialog still opens - and must - when a required parameter has no default, because the run
    /// cannot be built without it. That is what keeps the Deploy contract (M-051) intact without a
    /// special case: <c>candidateVersion</c> is declared required with no default, so a deployment
    /// always stops for an explicit, confirmed choice, and nothing is ever selected silently.
    /// </summary>
    /// <returns><c>Proceed</c> is false when the user cancelled, or when the declaration could not be read.</returns>
    public async Task<(bool Proceed, Dictionary<string, string>? Parameters)> ResolveDefaultParametersAsync(
        int pipelineId, string? sourceBranch = null)
    {
        List<PipelineRunParameterDto> declared;
        try { declared = await api.Pipelines.GetPipelineRunParametersAsync(pipelineId, sourceBranch).ConfigureAwait(false); }
        catch (Exception exception) when (IsTransportFailure(exception)) { return (false, null); }
        if (declared.Count == 0) return (true, null);

        if (declared.Any(NeedsAnAnswer))
        {
            var (releases, releaseTotal) = await LoadReleasesIfAParameterTakesOneAsync(pipelineId, declared);
            var answered = await dialog.OpenAsync<RunParametersDialog>(
                localizer["RunParameters"].Value,
                new Dictionary<string, object?>
                {
                    { "Parameters", declared },
                    { "AvailableReleases", releases },
                    { "AvailableReleaseTotal", releaseTotal }
                },
                new OmniDialogOptions { Width = DialogWidth(declared), AutoFocusFirstElement = false });
            return answered is Dictionary<string, string> values ? (true, values) : (false, null);
        }

        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in declared.Where(p => !string.IsNullOrWhiteSpace(p.Default)))
            defaults[parameter.Name] = parameter.Default!;
        return (true, defaults.Count > 0 ? defaults : null);
    }

    private static bool NeedsAnAnswer(PipelineRunParameterDto parameter) =>
        parameter.Required && string.IsNullOrWhiteSpace(parameter.Default);
}
