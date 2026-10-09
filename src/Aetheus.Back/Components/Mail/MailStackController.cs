// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Mail;

/// <summary>PLAN-005: TLS certificate, spam filter, delivery test, queue, mailbox usage, DNS verification
/// and diagnostics of a server's mail stack. Same route prefix and authorization model as
/// <see cref="MailController"/> (server Read for reads, server Write for every queued operation).</summary>
[ApiController]
[Route("api/servers/{serverId:int}/mail")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class MailStackController(
    IMailOperationsService operations,
    IMailDiagnosticsService diagnostics,
    IMailStackPurgeService purge,
    IResourceAuthorizationService authz) : ControllerBase
{
    /// <summary>"Réinstaller la messagerie": erases the whole mail stack. Same permission as the setup
    /// (server Write); the body types back the mail domain or host name.</summary>
    [HttpPost("purge")]
    public async Task<ActionResult<MailTaskQueuedDto>> Purge(int serverId, [FromBody] MailPurgeRequest request, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await purge.PurgeAsync(serverId, request, ct));
    }

    [HttpGet("certificate")]
    public async Task<ActionResult<MailCertificateDto>> GetCertificate(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await operations.GetCertificateAsync(serverId, ct));
    }

    /// <summary>Obtains the Let's Encrypt certificate of the mail hostname when missing, then installs it.</summary>
    [HttpPost("certificate")]
    public async Task<ActionResult<MailTaskQueuedDto>> RequestCertificate(
        int serverId, [FromBody] RequestMailCertificateRequest request, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.InstallCertificateAsync(serverId, request.Email, ct));
    }

    /// <summary>Installs an already issued lineage of the mail hostname (no ACME contact).</summary>
    [HttpPost("certificate/install")]
    public async Task<ActionResult<MailTaskQueuedDto>> InstallCertificate(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.InstallCertificateAsync(serverId, null, ct));
    }

    [HttpGet("spam")]
    public async Task<ActionResult<SpamFilterConfigDto>> GetSpamFilter(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await operations.GetSpamFilterAsync(serverId, ct));
    }

    [HttpPost("spam/install")]
    public async Task<ActionResult<MailTaskQueuedDto>> InstallSpamFilter(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.InstallSpamFilterAsync(serverId, ct));
    }

    [HttpPut("spam")]
    public async Task<ActionResult<MailTaskQueuedDto>> UpdateSpamFilter(
        int serverId, [FromBody] UpdateSpamFilterRequest request, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.UpdateSpamFilterAsync(serverId, request, ct));
    }

    [HttpPost("spam/learn")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<MailTaskQueuedDto>> LearnSpam(int serverId, [FromBody] LearnSpamRequest request, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.LearnSpamAsync(serverId, request, ct));
    }

    [HttpPost("test-delivery")]
    public async Task<ActionResult<MailTaskQueuedDto>> SendTest(int serverId, [FromBody] MailTestDeliveryRequest request, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.SendTestAsync(serverId, request, ct));
    }

    /// <summary>Queues a <c>postqueue -j</c> listing; the client parses the task log.</summary>
    [HttpPost("queue/refresh")]
    public async Task<ActionResult<MailTaskQueuedDto>> RefreshQueue(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await operations.RefreshQueueAsync(serverId, ct));
    }

    [HttpDelete("queue/{queueId:regex(^[[0-9A-Za-z]]{{6,32}}$)}")]
    public async Task<ActionResult<MailTaskQueuedDto>> DeleteQueuedMessage(int serverId, string queueId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.DeleteQueuedMessageAsync(serverId, queueId, ct));
    }

    [HttpPost("quota/refresh")]
    public async Task<ActionResult<MailTaskQueuedDto>> RefreshQuota(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await operations.RefreshQuotaAsync(serverId, ct));
    }

    /// <summary>Stores the mailbox sizes of a completed usage report task of this server.</summary>
    [HttpPost("quota/ingest/{taskId:int}")]
    public async Task<ActionResult<int>> IngestQuota(int serverId, int taskId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await operations.IngestQuotaReportAsync(serverId, taskId, ct));
    }

    [HttpPost("domains/{domainId:int}/dns/verify")]
    public async Task<ActionResult<MailDnsCheckDto>> VerifyDns(int serverId, int domainId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await diagnostics.VerifyDomainDnsAsync(serverId, domainId, ct));
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<MailDiagnosticsDto>> GetDiagnostics(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await diagnostics.GetAsync(serverId, ct));
    }

    [HttpGet("mx-preview")]
    public async Task<ActionResult<MailMxPreviewDto>> PreviewMx(int serverId, [FromQuery] string domain, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await diagnostics.PreviewMxAsync(domain, ct));
    }

    /// <summary>Recette R2-080: A record and reverse DNS of the host name the setup assistant proposes.</summary>
    [HttpGet("prerequisites")]
    public async Task<ActionResult<MailDiagnosticsDto>> CheckPrerequisites(int serverId, [FromQuery] string hostname, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Read, ct)) return Forbid();
        return Ok(await diagnostics.CheckPrerequisitesAsync(serverId, hostname, ct));
    }

    /// <summary>Recette R2-080: queues the outbound port 25 check; same permission as the setup it precedes.</summary>
    [HttpPost("prerequisites/outbound-smtp")]
    public async Task<ActionResult<MailTaskQueuedDto>> ProbeOutboundSmtp(int serverId, CancellationToken ct)
    {
        if (!await CanAsync(serverId, Permission.Write, ct)) return Forbid();
        return Ok(await operations.ProbeOutboundSmtpAsync(serverId, ct));
    }

    private Task<bool> CanAsync(int serverId, Permission permission, CancellationToken ct) =>
        authz.HasPermissionAsync(User, ResourceType.Server, serverId, permission, ct);
}
