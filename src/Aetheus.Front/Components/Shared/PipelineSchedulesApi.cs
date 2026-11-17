// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

/// <summary>One answer of the build planner's endpoints: the value on success, otherwise the status and the
/// reason the backend gave (the <c>message</c> of its error body). The planner shows a refusal itself, so
/// nothing here is a toast and nothing is swallowed.</summary>
public sealed record ScheduleCall<T>(T? Value, HttpStatusCode Status, string? Message)
    where T : class
{
    public bool IsSuccess => Value is not null;

    /// <summary>403: the caller may not change this schedule (or no longer may).</summary>
    public bool IsForbidden => Status == HttpStatusCode.Forbidden;

    /// <summary>409: what the caller read is no longer true (a stale value, or the repository changed).</summary>
    public bool IsConflict => Status == HttpStatusCode.Conflict;

    /// <summary>404: the pipeline is gone.</summary>
    public bool IsNotFound => Status == HttpStatusCode.NotFound;

    /// <summary>The same answer with the value changed by <paramref name="map"/> (nothing when it is a refusal).</summary>
    public ScheduleCall<TOut> Map<TOut>(Func<T, TOut> map)
        where TOut : class =>
        new(Value is null ? null : map(Value), Status, Message);
}

/// <summary>The build planner: the organization's schedule plan, the preview of a proposal and the write of a
/// pipeline's schedule. Each call sets <see cref="ErrorNotificationHandler.CallerHandlesErrors"/>: the page
/// explains every refusal in place, so the global handler must not add a second toast.</summary>
public sealed class PipelineSchedulesApi(HttpClient http) : ApiClientBase(http)
{
    /// <summary>The plan of the window. <paramref name="sourceBranches"/> (recette R-5) names, per pipeline id, the branch the
    /// reader chose to see that pipeline's schedule on: it changes what the plan shows and writes nothing.</summary>
    public async Task<ScheduleCall<SchedulePlanDto>> GetPlanAsync(
        int organizationId, DateTime fromUtc, DateTime toUtc, IReadOnlyDictionary<int, string>? sourceBranches = null, CancellationToken ct = default)
    {
        var branches = string.Concat((sourceBranches ?? new Dictionary<int, string>()).Select(choice =>
            $"&sourceBranch={Uri.EscapeDataString(choice.Key.ToString(CultureInfo.InvariantCulture) + ":" + choice.Value)}"));
        return (await SendAsync<SchedulePlanDto>(HttpMethod.Get,
            $"api/pipelines/schedule-plan?organizationId={organizationId}&from={Instant(fromUtc)}&to={Instant(toUtc)}{branches}", null, ct)
            .ConfigureAwait(false)).Map(Utc);
    }

    public async Task<ScheduleCall<SchedulePreviewDto>> GetPreviewAsync(
        int pipelineId, ScheduleFrequencySpec proposal, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var day = proposal.Day is { } value ? $"&day={value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        return (await SendAsync<SchedulePreviewDto>(HttpMethod.Get,
            $"api/pipelines/{pipelineId}/schedule-preview?frequency={proposal.Frequency}{day}"
            + $"&hour={(proposal.Hour ?? 0).ToString(CultureInfo.InvariantCulture)}"
            + $"&minute={(proposal.Minute ?? 0).ToString(CultureInfo.InvariantCulture)}"
            + $"&from={Instant(fromUtc)}&to={Instant(toUtc)}", null, ct).ConfigureAwait(false)).Map(Utc);
    }

    public Task<ScheduleCall<PipelineDto>> UpdateAsync(
        int pipelineId, UpdatePipelineScheduleRequest request, CancellationToken ct = default) =>
        SendAsync<PipelineDto>(HttpMethod.Put, $"api/pipelines/{pipelineId}/schedule", request, ct);

    /// <summary>Stores the branch a pipeline's definition is read on (recette R-5): its real source branch, not a display choice.</summary>
    public Task<ScheduleCall<PipelineDto>> UpdateSourceBranchAsync(
        int pipelineId, UpdatePipelineSourceBranchRequest request, CancellationToken ct = default) =>
        SendAsync<PipelineDto>(HttpMethod.Put, $"api/pipelines/{pipelineId}/source-branch", request, ct);

    private static string Instant(DateTime utc) =>
        Uri.EscapeDataString(AsUtc(utc).ToString("O", CultureInfo.InvariantCulture));

    // The application's JSON options hand every UTC instant back as browser-local time (the UTC to Local
    // converters of JsonOptions.Web). The planner converts from UTC to the reader's zone itself, and reading a
    // local instant as if it were UTC would shift every time by the offset: the instants are put back to UTC
    // here, once, so nothing after this has to know.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime? AsUtc(DateTime? value) => value is { } instant ? AsUtc(instant) : null;

    private static ScheduledTaskDto Utc(ScheduledTaskDto task) => task with
    {
        NextRunUtc = AsUtc(task.NextRunUtc),
        Occurrences = [.. task.Occurrences.Select(occurrence => occurrence with
        {
            StartUtc = AsUtc(occurrence.StartUtc),
            EndUtc = AsUtc(occurrence.EndUtc)
        })]
    };

    private static ScheduledServerActionDto Utc(ScheduledServerActionDto action) => action with
    {
        NextRunUtc = AsUtc(action.NextRunUtc),
        Occurrences = [.. action.Occurrences.Select(occurrence => occurrence with
        {
            StartUtc = AsUtc(occurrence.StartUtc),
            EndUtc = AsUtc(occurrence.EndUtc)
        })]
    };

    private static SchedulePlanDto Utc(SchedulePlanDto plan) => plan with
    {
        FromUtc = AsUtc(plan.FromUtc),
        ToUtc = AsUtc(plan.ToUtc),
        Tasks = [.. plan.Tasks.Select(Utc)],
        ServerActions = [.. plan.ServerActions.Select(Utc)]
    };

    private static SchedulePreviewDto Utc(SchedulePreviewDto preview) => preview with
    {
        FromUtc = AsUtc(preview.FromUtc),
        ToUtc = AsUtc(preview.ToUtc),
        Proposal = Utc(preview.Proposal)
    };

    private async Task<ScheduleCall<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions.Web);
        request.Options.Set(ErrorNotificationHandler.CallerHandlesErrors, true);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions.Web, ct).ConfigureAwait(false);
            return new ScheduleCall<T>(value, response.StatusCode, null);
        }

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new ScheduleCall<T>(null, response.StatusCode, ApiErrorText.Read(payload));
    }
}
