// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Back.Components.Settings;

namespace Aetheus.Back.Components.SystemLogs;

[ApiController]
[Route("api/system-logs")]
[Authorize(Roles = "Admin")]
public class SystemLogsController(ISystemLogService logService, ISettingsService settings) : ControllerBase
{
    [HttpGet("files")]
    public async Task<ActionResult<List<SystemLogFileDto>>> GetLogFiles(CancellationToken ct)
    {
        return Ok(await logService.GetLogFilesAsync(ct));
    }

    [HttpGet("entries")]
    public async Task<ActionResult<PaginatedResult<SystemLogEntryDto>>> GetLogEntries(
        [FromQuery] string? fileName,
        [FromQuery] string? level,
        [FromQuery, StringLength(200)] string? search,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery, StringLength(50)] string? sortBy = null,
        [FromQuery] bool sortDescending = true,
        CancellationToken ct = default,
        [FromQuery(Name = "Filters"), MaxLength(PaginationRequest.MaxFilters)] List<GridFilter>? filters = null)
    {
        page = Math.Max(1, page);
        pageSize = PaginationDefaults.Clamp(pageSize);
        // Recette R-453: the virtualized grid's column sort and header filters travel with each block.
        return Ok(await logService.GetLogEntriesAsync(fileName, level, search, dateFrom, dateTo, page, pageSize, ct,
            sortBy, sortDescending, filters is { Count: > 0 } ? filters : null));
    }

    [HttpDelete("purge")]
    public async Task<ActionResult<int>> PurgeOldLogs(
        [FromQuery, Range(1, 3650)] int? retentionDays = null,
        CancellationToken ct = default)
    {
        // S-FEAT-L9QM: when the caller omits an explicit window, fall back to the configurable
        // Retention:LogDays setting (default 30) so the Settings page actually governs purge depth.
        var days = retentionDays ?? await ResolveLogRetentionDaysAsync(ct);
        return Ok(await logService.PurgeOldLogsAsync(days, ct));
    }

    private async Task<int> ResolveLogRetentionDaysAsync(CancellationToken ct)
    {
        var configured = await settings.GetSettingValueAsync("Retention:LogDays", ct);
        return int.TryParse(configured, out var days) && days is >= 1 and <= 3650 ? days : 30;
    }

    [HttpGet("download/{fileName}")]
    public async Task<IActionResult> DownloadLogFile(
        [StringLength(255, MinimumLength = 1)] string fileName,
        CancellationToken ct)
    {
        var stream = await logService.DownloadLogFileAsync(fileName, ct);
        return File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
    }
}
