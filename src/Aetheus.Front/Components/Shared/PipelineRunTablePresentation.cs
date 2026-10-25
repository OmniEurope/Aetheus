// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

internal static class PipelineRunTablePresentation
{
    public static string PipelineHref(PipelineRunTableItem run, int? serverId) => serverId.HasValue
        ? $"/pipelines/{run.PipelineId}?serverId={serverId}"
        : run.ProjectId.HasValue
            ? $"/pipelines/{run.PipelineId}?projectId={run.ProjectId}"
            : $"/pipelines/{run.PipelineId}";

    public static string RunHref(PipelineRunTableItem run, int? serverId) => serverId.HasValue
        ? $"/pipelines/runs/{run.RunId}?serverId={serverId}"
        : run.ProjectId.HasValue
            ? $"/pipelines/runs/{run.RunId}?projectId={run.ProjectId}"
            : $"/pipelines/runs/{run.RunId}";

    /// <summary>Link to the branch: its Aetheus repository page when the run names an internal
    /// repository Aetheus resolved, otherwise the hosting web UI. Null (text) when the run has no
    /// branch, when the URL is an internal clone Aetheus could not resolve (the smart-HTTP endpoint
    /// has no branch page), or when it is not http(s) (a local path or an ssh remote).</summary>
    public static string? BranchHref(PipelineRunTableItem run)
    {
        if (string.IsNullOrWhiteSpace(run.BranchName)) return null;
        if (run.RepositoryId is { } repositoryId)
            return $"/git-repositories/{repositoryId}?tab=branches&branch={Uri.EscapeDataString(run.BranchName)}";
        if (!IsExternalWebUrl(run.RepositoryUrl))
            return null;
        return $"{WebBase(run.RepositoryUrl!)}/tree/{Uri.EscapeDataString(run.BranchName)}";
    }

    /// <summary>Link to the commit : its git-graph page when the run recorded it, its Aetheus
    /// repository page when the run names an internal repository Aetheus resolved, otherwise the
    /// hosting web UI. Same null (text) cases as the branch: an internal clone URL is never linked,
    /// it serves git, not a page.</summary>
    public static string? CommitHref(PipelineRunTableItem run)
    {
        if (string.IsNullOrWhiteSpace(run.CommitHash)) return null;
        if (run.CommitId is { } commitId) return $"/git/commits/{commitId}";
        if (run.RepositoryId is { } repositoryId)
            return $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(run.CommitHash)}";
        if (!IsExternalWebUrl(run.RepositoryUrl))
            return null;
        return $"{WebBase(run.RepositoryUrl!)}/commit/{Uri.EscapeDataString(run.CommitHash)}";
    }

    private static bool IsExternalWebUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && !GitRepositoryUrl.IsInternalClone(url);

    private static string WebBase(string repositoryUrl)
    {
        var baseUrl = repositoryUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryUrl[..^4]
            : repositoryUrl;
        return baseUrl.TrimEnd('/');
    }

    public static string FormatDuration(PipelineRunTableItem run) =>
        FormatDuration(run, DateTime.Now);

    public static string FormatDuration(PipelineRunTableItem run, DateTime currentTime)
    {
        if (run.StartedAt.Year < 2000)
            return "-";

        var completedAt = run.CompletedAt;
        if (!completedAt.HasValue && run.Status != PipelineStatus.Running)
            return "-";

        var duration = (completedAt ?? currentTime) - run.StartedAt;
        if (duration < TimeSpan.Zero)
            return "-";
        return DisplayFormatting.Duration(duration);
    }
}
