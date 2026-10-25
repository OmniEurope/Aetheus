// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-csharp-api.yml. Not compiled.

// ruleid: aetheus.csharp.controller-without-authorize
public class OpenReportsController : ControllerBase
{
    public IActionResult Get() => Ok();
}

// ok: aetheus.csharp.controller-without-authorize
[Authorize]
public class ClosedReportsController : ControllerBase
{
    public IActionResult Get() => Ok();
}

// ok: aetheus.csharp.controller-without-authorize
[ApiController]
[Authorize(Roles = "Admin")]
public class AdminReportsController : ControllerBase
{
    public IActionResult Get() => Ok();
}

// ok: aetheus.csharp.controller-without-authorize
[AllowAnonymous]
public class HealthController : ControllerBase
{
    public IActionResult Get() => Ok();
}

// ok: aetheus.csharp.controller-without-authorize
public class PerActionController : ControllerBase
{
    [Authorize]
    public IActionResult Write()
    {
        return Ok();
    }

    [AllowAnonymous]
    public IActionResult Read()
    {
        return Ok();
    }
}

[Authorize]
public class OverPostingCorpus : ControllerBase
{
    public async Task<IActionResult> Create([FromBody] User user, AppDbContext db)
    {
        // ruleid: aetheus.csharp.mass-assignment-bound-entity
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Ok();
    }

    public async Task<IActionResult> CreateFromDto([FromBody] CreateUserRequest request, AppDbContext db)
    {
        var user = new User { Name = request.Name };
        // ok: aetheus.csharp.mass-assignment-bound-entity
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Ok();
    }

    public async Task<IActionResult> Upload(IFormFile file, string folder)
    {
        // ruleid: aetheus.csharp.upload-client-file-name
        var path = Path.Combine("/srv/uploads", file.FileName);
        await using var stream = System.IO.File.Create(path);
        return Ok();
    }

    public async Task<IActionResult> UploadSafely(IFormFile file)
    {
        // ok: aetheus.csharp.upload-client-file-name
        var path = Path.Combine("/srv/uploads", Guid.NewGuid().ToString("N"));
        await using var stream = System.IO.File.Create(path);
        return Ok();
    }

    // ruleid: aetheus.csharp.antiforgery-disabled
    [IgnoreAntiforgeryToken]
    public IActionResult Submit() => Ok();

    // ok: aetheus.csharp.antiforgery-disabled
    [ValidateAntiForgeryToken]
    public IActionResult SubmitChecked() => Ok();

    // ruleid: aetheus.csharp.request-size-limit-disabled
    [DisableRequestSizeLimit]
    public IActionResult Import() => Ok();

    // ok: aetheus.csharp.request-size-limit-disabled
    [RequestSizeLimit(1048576)]
    public IActionResult ImportBounded() => Ok();
}

public class HostingCorpus
{
    public void Configure(WebApplication app, IWebHostEnvironment env, HubOptions hub)
    {
        // ruleid: aetheus.csharp.developer-exception-page-unguarded
        app.UseDeveloperExceptionPage();

        if (env.IsDevelopment())
        {
            // ok: aetheus.csharp.developer-exception-page-unguarded
            app.UseDeveloperExceptionPage();
            // ok: aetheus.csharp.detailed-errors-enabled
            hub.EnableDetailedErrors = true;
        }

        // ruleid: aetheus.csharp.detailed-errors-enabled
        hub.EnableDetailedErrors = true;
    }

    public void Limits(KestrelServerOptions kestrel, FormOptions form)
    {
        // ruleid: aetheus.csharp.request-size-limit-disabled
        kestrel.Limits.MaxRequestBodySize = null;
        // ok: aetheus.csharp.request-size-limit-disabled
        kestrel.Limits.MaxRequestBodySize = 10485760;
        // ruleid: aetheus.csharp.request-size-limit-disabled
        form.MultipartBodyLengthLimit = long.MaxValue;
    }

    public void Headers(HttpResponse response)
    {
        // ruleid: aetheus.csharp.security-header-relaxed
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'";
        // ruleid: aetheus.csharp.security-header-relaxed
        response.Headers.Append("Access-Control-Allow-Origin", "*");
        // ok: aetheus.csharp.security-header-relaxed
        response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'nonce-abc'";
        // ok: aetheus.csharp.security-header-relaxed
        response.Headers.Append("Access-Control-Allow-Origin", "https://app.example.org");
    }

    public void Extract(ZipArchive archive, string destination)
    {
        foreach (var entry in archive.Entries)
        {
            // ruleid: aetheus.csharp.archive-entry-path-traversal
            var target = Path.Combine(destination, entry.FullName);
            entry.ExtractToFile(target);
        }
    }

    public void ExtractChecked(ZipArchive archive, string destination)
    {
        foreach (var entry in archive.Entries)
        {
            // ok: aetheus.csharp.archive-entry-path-traversal
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destination, StringComparison.Ordinal))
                throw new InvalidDataException("Entry escapes the destination.");
            entry.ExtractToFile(target);
        }
    }

    public byte[] HashPassword(string password, string title)
    {
        // ruleid: aetheus.csharp.password-fast-hash
        var one = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        // ok: aetheus.csharp.password-fast-hash
        var two = SHA256.HashData(Encoding.UTF8.GetBytes(title));
        return one;
    }

    // ruleid: aetheus.csharp.hard-coded-credential
    private const string AdminPassword = "Sup3rS3cretValue";
    // ruleid: aetheus.csharp.hard-coded-credential
    private string apiKey = "aaaa1111aaaa1111";
    // ok: aetheus.csharp.hard-coded-credential
    private const string PasswordHeaderName = "X-Password-Header1";
    // ok: aetheus.csharp.hard-coded-credential
    private const string PasswordSettingPath = "Admin:Password";
    // ok: aetheus.csharp.hard-coded-credential
    private string secret = Environment.GetEnvironmentVariable("APP_SECRET");

    // ruleid: aetheus.csharp.connection-string-password-literal
    private const string Connection = "Host=db;Database=app;Username=app;Password=hunter2secret";
    // ok: aetheus.csharp.connection-string-password-literal
    private const string Template = "Host=db;Database=app;Username=app;Password={0}";
}
