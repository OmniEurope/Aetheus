// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using System.Text.Json;

namespace Aetheus.Agent.Core.Services;

/// <summary>Recette R-8: asks the backend, over the agent's authenticated channel, whether a reboot order may still be
/// executed (schedule enabled and unchanged, current order, within its deadline, server idle).</summary>
public interface IRebootOrderConfirmer
{
    /// <summary>The backend's answer, or null when it could not be obtained: the caller then refuses the reboot.</summary>
    Task<RebootOrderConfirmationDto?> ConfirmAsync(int taskId, CancellationToken ct);
}

internal sealed class RebootOrderConfirmer(IHttpClientFactory httpClientFactory, ILogger<RebootOrderConfirmer> logger)
    : IRebootOrderConfirmer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RebootOrderConfirmationDto?> ConfirmAsync(int taskId, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("AetheusServer");
            using var response = await client.PostAsync($"api/servers/agent/reboot-orders/{taskId}/confirm", content: null, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Reboot order {TaskId}: the backend answered {Status} to the confirmation", taskId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RebootOrderConfirmationDto>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Fail closed: without a confirmation the host is not rebooted.
            logger.LogWarning(ex, "Reboot order {TaskId}: the confirmation could not be obtained", taskId);
            return null;
        }
    }
}
