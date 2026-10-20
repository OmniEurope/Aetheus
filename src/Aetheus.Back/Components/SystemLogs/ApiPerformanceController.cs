// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;
using OmniEurope.Performance;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>Expose platform API timings to administrators only because route and timing data are
/// not project-scoped.</summary>
[ApiController]
[Route(ApiPerformanceReport.ReportRoute)]
[Authorize(Roles = "Admin")]
// Reading the report is never measured, so it never announces a change of itself.
[DisableHttpMetrics]
public class ApiPerformanceController(RequestPerformanceRecorder recorder) : ControllerBase
{
    [HttpGet]
    public ActionResult<ApiPerformanceReportDto> Get() => Ok(ApiPerformanceReport.Build(recorder));
}
