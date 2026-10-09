// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;
using OmniEurope.Performance;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>PLAN-003 lot 27 / D27: the API's own timings, for the administration Performance page.
/// Admin only: route templates and timings describe the platform, not one project.</summary>
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
