// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The alert a refused schedule write (or a plan that could not be read) is said in, with the backend's reason.</summary>
public partial class PipelineScheduleRefusal
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The HTTP status of the refusal.</summary>
    [Parameter] public HttpStatusCode Status { get; set; }

    /// <summary>The backend's reason, when it gave one.</summary>
    [Parameter] public string? Message { get; set; }

    /// <summary>Raised by "Reload".</summary>
    [Parameter] public EventCallback OnReload { get; set; }

    private bool OffersReload => Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound;

    private string Title => Status switch
    {
        HttpStatusCode.Forbidden => L["ScheduleForbiddenTitle"].Value,
        HttpStatusCode.Conflict => L["ScheduleConflictTitle"].Value,
        HttpStatusCode.NotFound => L["ScheduleNotFoundTitle"].Value,
        _ => L["ScheduleWriteFailedTitle"].Value
    };

    private string Text
    {
        get
        {
            var sentence = Status switch
            {
                HttpStatusCode.Forbidden => L["ScheduleForbiddenText"].Value,
                HttpStatusCode.Conflict => L["ScheduleConflictText"].Value,
                HttpStatusCode.NotFound => L["ScheduleNotFoundText"].Value,
                _ => string.Empty
            };
            return string.IsNullOrWhiteSpace(Message) ? sentence : $"{sentence} {Message}".Trim();
        }
    }
}
