// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>What fills the route parameters of the inventory: a value per entity, or the reason an entity has none.</summary>
internal sealed record ResolvedEntities(
    IReadOnlyDictionary<PageEntity, string> Values,
    IReadOnlyDictionary<PageEntity, string> Missing);

/// <summary>
/// Looks the route parameters up in the seeded E2E data through the back end API, with the session of the page it is
/// given. It always picks the entity with the smallest identifier: the seeded rows come first, so a row created by
/// another test never changes which page is measured.
/// </summary>
internal sealed class PageEntityResolver(IPage page, string backendUrl)
{
    // The article opened from the home page and the settings tab that every administrator has.
    private const string HelpArticleKey = "dashboard";
    private const string SettingsTabKey = "security";
    private const int GitGraphProbeLimit = 10;

    private readonly Dictionary<PageEntity, string> _values = [];
    private readonly Dictionary<PageEntity, string> _missing = [];
    private string? _token;

    public async Task<ResolvedEntities> ResolveAsync()
    {
        var servers = await ListAsync("servers");
        var repositories = await ListAsync("git/repos");
        var projects = await ListAsync("projects");
        var pipelines = await ListAsync("pipelines");
        var templates = await ListAsync("pipelines/templates");

        // The project that owns the first git repository is the one with the richest data (sources, pipelines, runs).
        JsonElement? repository = repositories.Items.Count == 0 ? null : repositories.Items.OrderBy(Id).First();
        var repositoryId = repository is { } firstRepository ? Id(firstRepository) : (long?)null;
        Set(PageEntity.GitRepository, repositoryId, $"no git repository is seeded{repositories.Problem}");
        var projectId = repository is { } owner ? owner.GetProperty("projectId").GetInt64() : MinId(projects.Items);
        Set(PageEntity.Project, projectId, $"no project is seeded{projects.Problem}");
        Set(PageEntity.Server, MinId(servers.Items), $"no server is seeded{servers.Problem}");
        Set(PageEntity.Pipeline, MinId(pipelines.Items), $"no pipeline is seeded{pipelines.Problem}");
        Set(PageEntity.PipelineTemplate, MinId(templates.Items), $"no pipeline template is seeded{templates.Problem}");

        foreach (var (entity, endpoint) in new (PageEntity, string)[]
                 {
                     (PageEntity.Environment, "environments"),
                     (PageEntity.Vault, "vaults"),
                     (PageEntity.VariableLibrary, "variable-libraries"),
                     (PageEntity.Release, "releases"),
                     (PageEntity.User, "users"),
                     (PageEntity.Role, "roles"),
                     (PageEntity.Organization, "organizations"),
                     (PageEntity.Dashboard, "dashboards"),
                     (PageEntity.TaskItem, "tasks"),
                     (PageEntity.AiTask, "ai/tasks")
                 })
        {
            var list = await ListAsync(endpoint);
            Set(entity, MinId(list.Items), $"nothing is seeded behind api/{endpoint}{list.Problem}");
        }

        await ResolveLinkedPipelineAsync();
        await ResolvePipelineRunAsync(pipelines.Items);
        await ResolvePipelineApprovalAsync(pipelines.Items);
        await ResolveTemplateVersionAsync(templates.Items);
        await ResolveArtifactAsync(projectId);
        await ResolveMailAccountAsync(servers.Items);
        await ResolveAlertOccurrenceAsync();
        await ResolveAnalysisFindingAsync();
        await ResolveGitObjectsAsync(repositoryId);

        _values[PageEntity.HelpArticleKey] = HelpArticleKey;
        _values[PageEntity.SettingsTab] = SettingsTabKey;
        return new ResolvedEntities(_values, _missing);
    }

    private async Task ResolveLinkedPipelineAsync()
    {
        // Promoting or updating from a template only exists for a pipeline that was created from one: for the others the
        // back end answers 403, which the browser logs as a console error.
        var fleet = await ListAsync("pipelines/fleet");
        var linked = fleet.Items
            .Where(item => item.TryGetProperty("templateId", out var template) && template.ValueKind == JsonValueKind.Number)
            .Select(item => item.TryGetProperty("pipelineId", out var pipeline) ? pipeline : item.GetProperty("id"))
            .Select(identifier => identifier.GetInt64())
            .ToList();
        Set(PageEntity.PipelineLinkedToTemplate, linked.Count == 0 ? null : linked.Min(),
            $"no seeded pipeline was created from a template, so none can be promoted or updated{fleet.Problem}");
    }

    private async Task ResolvePipelineRunAsync(IReadOnlyList<JsonElement> pipelines)
    {
        foreach (var pipeline in pipelines.OrderBy(Id))
        {
            var runs = await ListAsync($"pipelines/{Id(pipeline).ToString(CultureInfo.InvariantCulture)}/runs");
            if (MinId(runs.Items) is { } run)
            {
                Set(PageEntity.PipelineRun, run, string.Empty);
                return;
            }
        }
        Set(PageEntity.PipelineRun, null, "no pipeline run is seeded");
    }

    private async Task ResolvePipelineApprovalAsync(IReadOnlyList<JsonElement> pipelines)
    {
        // The page of an approval link needs a run that raised an approval request, and the identifier of that request.
        foreach (var pipeline in pipelines.OrderBy(Id))
        {
            var runs = await ListAsync($"pipelines/{Id(pipeline).ToString(CultureInfo.InvariantCulture)}/runs");
            foreach (var run in runs.Items.OrderBy(Id))
            {
                var approvals = await ListAsync($"pipelines/runs/{Id(run).ToString(CultureInfo.InvariantCulture)}/approvals");
                if (MinId(approvals.Items) is not { } approval) continue;
                Set(PageEntity.PipelineApprovalRun, Id(run), string.Empty);
                Set(PageEntity.PipelineApproval, approval, string.Empty);
                return;
            }
        }
        Set(PageEntity.PipelineApprovalRun, null, "no seeded pipeline run raised an approval request");
        Set(PageEntity.PipelineApproval, null, "no seeded pipeline run raised an approval request");
    }

    private async Task ResolveTemplateVersionAsync(IReadOnlyList<JsonElement> templates)
    {
        foreach (var template in templates.OrderBy(Id))
        {
            var versions = await ListAsync($"pipelines/templates/{Id(template).ToString(CultureInfo.InvariantCulture)}/versions");
            var numbers = versions.Items.Select(item => item.GetProperty("version").GetInt64()).Order().ToList();
            if (numbers.Count >= 2)
            {
                Set(PageEntity.PipelineTemplateTargetVersion, numbers[^1], string.Empty);
                return;
            }
        }
        Set(PageEntity.PipelineTemplateTargetVersion, null,
            "every seeded template has a single version, so no pipeline can be offered an update");
    }

    private async Task ResolveArtifactAsync(long? projectId)
    {
        var artifacts = projectId is null
            ? ListResult.None
            : await ListAsync($"artifacts/project/{projectId.Value.ToString(CultureInfo.InvariantCulture)}");
        Set(PageEntity.Artifact, MinId(artifacts.Items), $"no artifact is seeded{artifacts.Problem}");
    }

    private async Task ResolveMailAccountAsync(IReadOnlyList<JsonElement> servers)
    {
        foreach (var server in servers.OrderBy(Id))
        {
            var accounts = await ListAsync($"servers/{Id(server).ToString(CultureInfo.InvariantCulture)}/mail/accounts");
            if (MinId(accounts.Items) is { } account)
            {
                Set(PageEntity.ServerMailAccount, account, string.Empty);
                return;
            }
        }
        Set(PageEntity.ServerMailAccount, null, "no mail account is seeded on any server");
    }

    private async Task ResolveAlertOccurrenceAsync()
    {
        // No API lists alert occurrences; one can only exist once an alert has fired.
        var alerts = await ListAsync("alerts");
        var fired = alerts.Items.Any(alert => alert.TryGetProperty("lastTriggeredAt", out var at) && at.ValueKind != JsonValueKind.Null);
        Set(PageEntity.AlertOccurrence, null, fired
            ? "an alert has fired but no API lists its occurrences, so the harness cannot find an identifier"
            : "no seeded alert has ever fired, so there is no alert occurrence to open");
    }

    private async Task ResolveAnalysisFindingAsync()
    {
        // The portfolio lists the findings a project carries; empty means that nothing was analysed.
        var portfolio = await ListAsync("analysis/portfolio");
        Set(PageEntity.AnalysisFinding, null, portfolio.Items.Count == 0
            ? "the analysis portfolio is empty, so no analysis finding is seeded"
            : "the portfolio has rows but no API the harness uses lists finding identifiers");
    }

    private async Task ResolveGitObjectsAsync(long? repositoryId)
    {
        if (repositoryId is null)
        {
            _missing[PageEntity.GitCommitSha] = "no git repository is seeded";
        }
        else
        {
            var commits = await ListAsync($"git/repos/{repositoryId.Value.ToString(CultureInfo.InvariantCulture)}/commits");
            var sha = commits.Items.Count == 0 ? null : commits.Items[0].GetProperty("sha").GetString();
            if (sha is null) _missing[PageEntity.GitCommitSha] = "the seeded git repository has no commit";
            else _values[PageEntity.GitCommitSha] = sha;
        }

        // The pages of a commit or a branch by numeric identifier read the git graph tables, which have no list API.
        Set(PageEntity.GitCommit, await ProbeGitGraphAsync("commits"),
            $"no commit row is seeded in the git graph (api/gitgraph/commits answers 404 for identifiers 1 to {GitGraphProbeLimit})");
        Set(PageEntity.GitBranch, await ProbeGitGraphAsync("branches"),
            $"no branch row is seeded in the git graph (api/gitgraph/branches answers 404 for identifiers 1 to {GitGraphProbeLimit})");
    }

    private async Task<long?> ProbeGitGraphAsync(string kind)
    {
        for (var id = 1; id <= GitGraphProbeLimit; id++)
        {
            var response = await GetAsync($"gitgraph/{kind}/{id.ToString(CultureInfo.InvariantCulture)}");
            if (response.Status == 200) return id;
        }
        return null;
    }

    private void Set(PageEntity entity, long? id, string missingReason)
    {
        if (id is { } value) _values[entity] = value.ToString(CultureInfo.InvariantCulture);
        else _missing[entity] = missingReason;
    }

    private static long Id(JsonElement item) => item.GetProperty("id").GetInt64();

    private static long? MinId(IReadOnlyList<JsonElement> items) => items.Count == 0 ? null : items.Min(Id);

    private async Task<ListResult> ListAsync(string endpoint)
    {
        var response = await GetAsync(endpoint);
        if (response.Status != 200 || response.Body is not { } body) return new ListResult([], $" (api/{endpoint} answered {response.Status})");
        var items = body.ValueKind == JsonValueKind.Array
            ? body
            : body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var inner) ? inner : default;
        return items.ValueKind == JsonValueKind.Array
            ? new ListResult(items.EnumerateArray().Select(element => element.Clone()).ToList(), string.Empty)
            : new ListResult([], $" (api/{endpoint} answered a body that is not a list)");
    }

    // Asked through the context's request client, not with fetch in the page: a 404 answered to a probe must not
    // end up as a console error, which the base class counts against the test.
    private async Task<ApiResponse> GetAsync(string endpoint)
    {
        _token ??= await page.EvaluateAsync<string>("() => localStorage.getItem('aetheus_auth_token')")
            ?? throw new InvalidOperationException("The signed-in session has no access token in local storage.");
        var response = await page.Context.APIRequest.GetAsync($"{backendUrl}/api/{endpoint}", new()
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" },
            IgnoreHTTPSErrors = true,
            FailOnStatusCode = false
        });
        return new ApiResponse(response.Status, response.Ok ? await response.JsonAsync() : null);
    }

    private sealed record ApiResponse(int Status, JsonElement? Body);

    /// <summary>The rows of a list endpoint. <see cref="Problem"/> is empty when the endpoint answered a list, else says
    /// what it answered; it ends the reason of a missing entity and never holds a count, so the reason stays stable.</summary>
    private sealed record ListResult(IReadOnlyList<JsonElement> Items, string Problem)
    {
        public static ListResult None { get; } = new([], " (no project to ask for its artifacts)");
    }
}
