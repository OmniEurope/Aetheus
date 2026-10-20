// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Plugins;

[ApiController]
[Route("api/plugins")]
[Authorize(Roles = "Admin")]
public class PluginsController(IPluginService pluginService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResult<PluginRegistrationDto>>> GetPlugins(
        [FromQuery] PaginationRequest request, CancellationToken ct)
    {
        return Ok(await pluginService.GetPluginsPageAsync(request, ct));
    }

    /// <summary>The authors the plugins list's Author column filter offers.</summary>
    [HttpGet("filter-values")]
    public async Task<ActionResult<PluginFilterValuesDto>> GetFilterValues(CancellationToken ct) =>
        Ok(await pluginService.GetFilterValuesAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PluginRegistrationDto>> GetPlugin(int id, CancellationToken ct)
    {
        var result = await pluginService.GetPluginAsync(id, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<PluginRegistrationDto>> RegisterPlugin([FromBody] RegisterPluginRequest request, CancellationToken ct)
    {
        var result = await pluginService.RegisterPluginAsync(request, ct);
        return CreatedAtAction(nameof(GetPlugin), new { id = result.Id }, result);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PluginRegistrationDto>> UpdatePlugin(int id, [FromBody] UpdatePluginRequest request, CancellationToken ct)
    {
        var result = await pluginService.UpdatePluginAsync(id, request, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> UnregisterPlugin(int id, CancellationToken ct)
    {
        var deleted = await pluginService.UnregisterPluginAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
