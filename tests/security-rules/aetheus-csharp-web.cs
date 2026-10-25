// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-csharp-web.yml, C# rules. Not compiled.
public class WebRuleCorpus : Controller
{
    public void Tokens(JwtBearerOptions options)
    {
        // ruleid: aetheus.csharp.jwt-validation-disabled
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = false, ValidateAudience = true };
        // ruleid: aetheus.csharp.jwt-validation-disabled
        options.TokenValidationParameters.ValidateLifetime = false;
        // ruleid: aetheus.csharp.jwt-validation-disabled
        options.TokenValidationParameters.RequireSignedTokens = false;
        // ok: aetheus.csharp.jwt-validation-disabled
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true };

        // ruleid: aetheus.csharp.jwt-https-metadata-disabled
        options.RequireHttpsMetadata = false;
        // ok: aetheus.csharp.jwt-https-metadata-disabled
        options.RequireHttpsMetadata = true;
    }

    public void Cors(CorsOptions options)
    {
        // ruleid: aetheus.csharp.cors-any-origin-with-credentials
        options.AddPolicy("open", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowCredentials());
        // ruleid: aetheus.csharp.cors-any-origin-with-credentials
        options.AddPolicy("reflect", policy => policy.SetIsOriginAllowed(_ => true).AllowCredentials());
        // ok: aetheus.csharp.cors-any-origin-with-credentials
        options.AddPolicy("listed", policy => policy.WithOrigins("https://app.example.org").AllowCredentials());
        // ruleid: aetheus.csharp.cors-allow-any-origin
        options.AddPolicy("public", policy => policy.AllowAnyOrigin().AllowAnyMethod());
        // ok: aetheus.csharp.cors-allow-any-origin
        options.AddPolicy("strict", policy => policy.WithOrigins("https://app.example.org").AllowAnyMethod());
    }

    public void Cookies(CookieOptions options, CookieAuthenticationOptions authentication)
    {
        // ruleid: aetheus.csharp.cookie-secure-disabled
        var plain = new CookieOptions { Secure = false, HttpOnly = true };
        // ruleid: aetheus.csharp.cookie-secure-disabled
        authentication.Cookie.SecurePolicy = CookieSecurePolicy.None;
        // ok: aetheus.csharp.cookie-secure-disabled
        authentication.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        // ruleid: aetheus.csharp.cookie-httponly-disabled
        var readable = new CookieOptions { Secure = true, HttpOnly = false };
        // ruleid: aetheus.csharp.cookie-httponly-disabled
        options.HttpOnly = false;
        // ok: aetheus.csharp.cookie-httponly-disabled
        options.HttpOnly = true;

        // ruleid: aetheus.csharp.cookie-samesite-none
        options.SameSite = SameSiteMode.None;
        // ok: aetheus.csharp.cookie-samesite-none
        options.SameSite = SameSiteMode.Strict;
    }

    public void WriteCookies(string value, CookieOptions options)
    {
        // ruleid: aetheus.csharp.cookie-without-options
        Response.Cookies.Append("session", value);
        // ok: aetheus.csharp.cookie-without-options
        Response.Cookies.Append("session", value, options);
    }

    public IActionResult Login(string returnUrl)
    {
        // ruleid: aetheus.csharp.open-redirect
        return Redirect(returnUrl);
    }

    public void Logout(string next)
    {
        // ruleid: aetheus.csharp.open-redirect
        Response.Redirect(next);
    }

    public IActionResult LoginChecked(string returnUrl)
    {
        if (!Url.IsLocalUrl(returnUrl))
            return BadRequest();
        // ok: aetheus.csharp.open-redirect
        return Redirect(returnUrl);
    }

    public IActionResult LoginLocal(string returnUrl)
    {
        // ok: aetheus.csharp.open-redirect
        return LocalRedirect(returnUrl);
    }

    public IActionResult Home()
    {
        // ok: aetheus.csharp.open-redirect
        return Redirect("/home");
    }

    public MarkupString Render(string comment, string title)
    {
        // ruleid: aetheus.csharp.blazor-markupstring-dynamic
        var one = (MarkupString)comment;
        // ruleid: aetheus.csharp.blazor-markupstring-dynamic
        return new MarkupString("<b>" + comment + "</b>");
    }

    public MarkupString RenderEncoded(string comment)
    {
        var encoded = WebUtility.HtmlEncode(comment);
        // ok: aetheus.csharp.blazor-markupstring-dynamic
        return (MarkupString)("<b>" + encoded + "</b>");
    }

    public MarkupString RenderConstant()
    {
        // ok: aetheus.csharp.blazor-markupstring-dynamic
        return (MarkupString)"<hr />";
    }

    public void Logging(ILogger logger, string password, string apiKey, string userName, CancellationToken cancellationToken)
    {
        // ruleid: aetheus.csharp.secret-in-log
        logger.LogInformation("signed in with {Password}", password);
        // ruleid: aetheus.csharp.secret-in-log
        logger.LogWarning("calling with {Key}", apiKey);
        // ok: aetheus.csharp.secret-in-log
        logger.LogInformation("signed in as {User}", userName);
        // ok: aetheus.csharp.secret-in-log
        logger.LogDebug("cancelled {Token}", cancellationToken);
    }
}
