// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Aetheus.WebAnalytics.Pages;

public sealed class AudienceMeasurementModel(AetheusWebAnalyticsOptions options) : PageModel
{
    public AetheusWebAnalyticsOptions Options => options;
    public bool OptedOut { get; private set; }

    public async Task OnGetAsync() =>
        OptedOut = await AnalyticsPrivacyPolicy.IsOptedOutAsync(HttpContext, options).ConfigureAwait(false);

    public async Task<IActionResult> OnPostAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (options.AuthenticatedOptOutWriter is null)
                return StatusCode(StatusCodes.Status501NotImplemented);
            await options.AuthenticatedOptOutWriter(HttpContext, true).ConfigureAwait(false);
        }
        else
        {
            Response.Cookies.Append(
                AnalyticsPrivacyPolicy.OptOutCookieName,
                "1",
                AetheusWebAnalyticsEndpointExtensions.PreferenceCookieOptions());
        }
        OptedOut = true;
        return Page();
    }
}
