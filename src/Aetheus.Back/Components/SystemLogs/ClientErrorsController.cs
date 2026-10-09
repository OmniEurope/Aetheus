// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.SystemLogs;

[ApiController]
[Route("api/client-errors")]
[Authorize]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Report([FromBody] ClientErrorLogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Every field is caller-controlled, so a newline in any of them would forge whole log lines
        // in a text sink and let a report impersonate an unrelated event. Lengths are already bounded
        // on the DTO; what was missing is that a value must stay ONE line. The page address loses its
        // query string here too (the client already drops it): a token in a URL never reaches the log.
        logger.LogError(
            "Client error at {ClientPath}: {Summary} - {ClientMessage}; correlation {CorrelationId}; stack {ClientStackTrace}",
            PageAddress(request.Path),
            SingleLine(request.Summary),
            SingleLine(request.Message),
            SingleLine(request.CorrelationId),
            SingleLine(request.StackTrace) ?? "(none)");
        return NoContent();
    }

    private static string PageAddress(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "(unknown)"
            : SingleLine(ClientErrorLogRequest.PathWithoutQuery(path))!;

    private static string? SingleLine(string? value) => value?.ReplaceLineEndings(" ");
}
