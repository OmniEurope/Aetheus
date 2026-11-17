// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-59, R-67 (user decision 2026-11-03): what the wizard declares for the commands its QA pipelines run, so the user fills in
/// nothing by hand. The QA pipeline's browser tests are the Playwright or Cypress tests the repository holds (the backend read
/// its <c>package.json</c> files) or its .NET Playwright project (a project file that references <c>Microsoft.Playwright</c>); a
/// repository read to the end that holds none declares <see cref="None"/>, a choice the QA pipeline accepts without a warning
/// (the wizard warns about it on its Capabilities step). What was not read declares nothing: the run then says the command is
/// missing, never a <c>none</c> the wizard could not know. The commands run in the unpacked source of the deployed release
/// (<c>QA_SOURCE</c>) against its front (<c>QA_FRONT_URL</c>), the two values the QA pipeline hands to them.
/// </summary>
internal static partial class PipelineSetupBrowserTests
{
    /// <summary>The value that declares the project has no such command: the run says so and does not warn.</summary>
    public const string None = "none";

    /// <summary>A directory of the repository as the backend lists it, safe to write between double quotes in a shell command.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9._\-]+(/[A-Za-z0-9._\-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDirectory();

    /// <summary>A target framework moniker (<c>net10.0</c>, <c>net10.0-windows</c>) safe to write in a shell command.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9.\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFramework();

    /// <summary>The Playwright for .NET image (browsers in <c>/ms-playwright</c>, their system libraries, the .NET SDK and
    /// PowerShell), the one <c>deploy/docker/Dockerfile.e2e</c> builds the platform's own browser tests on.</summary>
    private const string PlaywrightDotNetImage =
        "mcr.microsoft.com/playwright/dotnet:v1.64.1-noble@sha256:6955861f6e17b2580c00f0970198e446f006cdae6c34fb8f9ad5318aaab50da0";

    /// <summary>The Playwright image with Node.js, browsers in <c>/ms-playwright</c> and their system libraries.</summary>
    private const string PlaywrightNodeImage =
        "mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f";

    /// <summary>The Cypress image with Node.js, Chrome and the libraries Cypress and its Xvfb need.</summary>
    private const string CypressImage =
        "cypress/browsers:node-24.22.1-chrome-156.0.8078.73-1-ff-159.0-edge-156.0.4314.50-1@sha256:348cf0242c31155e85b2a90fdd5f95afc1f3aaeca830ba98acadb3ce5d77c4e0";

    /// <summary>
    /// R-20 (user decision 2026-11-10, option 1): the value of <c>APPLICATION_QA_E2E_IMAGE</c> the wizard writes beside the commands,
    /// the official pinned image of the repository's browser test tool. The commands run in it, on the host's network, with
    /// the sealed source mounted: the browser and its system libraries (the <c>libgbm.so.1</c> a non-root agent could not
    /// install) come with the image, so nothing is installed on the agent. Null whenever <see cref="Commands"/> declares no
    /// command to run (nothing known, or <see cref="None"/>).
    /// </summary>
    public static string? Image(PipelineSetupDetectionDto? detection) =>
        Commands(detection) is null or None
            ? null
            : detection!.BrowserTestTool switch
            {
                "playwright-dotnet" => PlaywrightDotNetImage,
                "playwright" => PlaywrightNodeImage,
                "cypress" => CypressImage,
                _ => null
            };

    /// <summary>The value of <c>APPLICATION_QA_E2E_COMMANDS</c> the wizard writes, or null to write none (nothing known).</summary>
    public static string? Commands(PipelineSetupDetectionDto? detection)
    {
        if (detection is null) return null;
        if (detection.HasBrowserTests == false) return None;
        if (detection.HasBrowserTests != true) return null;
        if (detection.BrowserTestTool == "playwright-dotnet") return DotNetPlaywrightCommands(detection);
        var directory = detection.BrowserTestDirectory ?? string.Empty;
        if (directory.Length > 0 && !SafeDirectory().IsMatch(directory)) return null;
        var install = "if test -f package-lock.json; then npm ci; else npm install; fi";
        var start = directory.Length == 0 ? "cd \"$QA_SOURCE\"" : $"cd \"$QA_SOURCE/{directory}\"";
        return detection.BrowserTestTool switch
        {
            "playwright" => $"{start} && {install} && npx playwright install chromium && "
                + "PLAYWRIGHT_BASE_URL=\"$QA_FRONT_URL\" BASE_URL=\"$QA_FRONT_URL\" npx playwright test",
            "cypress" => $"{start} && {install} && CYPRESS_BASE_URL=\"$QA_FRONT_URL\" npx cypress run",
            _ => null
        };
    }

    /// <summary>
    /// The commands of a .NET Playwright project, run by <c>sh -eu -c</c> (several lines, written as a block scalar): release
    /// build of that project; the browser installed by the <c>playwright.ps1</c> of its build folder, without <c>--with-deps</c>
    /// (the commands run in the browser image of <see cref="Image"/>, which carries PowerShell and the system libraries, so
    /// nothing is installed on the agent and no root is needed); <c>dotnet test --no-build</c> with
    /// <c>QA_FRONT_URL</c> and <c>QA_BACK_URL</c> in its environment; a failure when no <c>.trx</c> result file was produced,
    /// whatever the exit code. Everything is in the pipeline's YAML, no versioned script. The test options follow the runner the
    /// repository's <c>global.json</c> selects (Microsoft.Testing.Platform, else VSTest). Null when the project file or its
    /// framework is not safe to write into a command.
    /// </summary>
    private static string? DotNetPlaywrightCommands(PipelineSetupDetectionDto detection)
    {
        var project = detection.BrowserTestProject;
        if (project is null || !SafeDirectory().IsMatch(project)) return null;
        var framework = detection.BrowserTestFramework;
        if (framework is { Length: > 0 } && !SafeFramework().IsMatch(framework)) return null;
        var directory = detection.BrowserTestDirectory ?? string.Empty;
        if (directory.Length > 0 && !SafeDirectory().IsMatch(directory)) return null;
        var buildFolder = directory.Length == 0 ? "bin/Release" : $"{directory}/bin/Release";
        var script = framework is { Length: > 0 }
            ? $"PLAYWRIGHT_SCRIPT=\"{buildFolder}/{framework}/playwright.ps1\""
            : $"PLAYWRIGHT_SCRIPT=\"$(find \"{buildFolder}\" -name playwright.ps1 | head -n 1)\"\n"
                + "test -n \"$PLAYWRIGHT_SCRIPT\" || { echo \"playwright.ps1 is not in the build folder of $E2E_PROJECT.\" >&2; exit 1; }";
        var test = detection.BrowserTestUsesTestingPlatform == true
            ? "\"$DOTNET\" test --project \"$E2E_PROJECT\" --configuration Release --no-build "
                + "--results-directory \"$RESULTS\" --report-trx --report-trx-filename e2e.trx || STATUS=$?"
            : "\"$DOTNET\" test \"$E2E_PROJECT\" --configuration Release --no-build "
                + "--results-directory \"$RESULTS\" --logger \"trx;LogFileName=e2e.trx\" || STATUS=$?";
        return string.Join('\n',
            "set -eu",
            "test -n \"${QA_FRONT_URL:-}\" || { echo \"QA_FRONT_URL is empty: the stack published no front port.\" >&2; exit 1; }",
            "QA_BACK_URL=\"${QA_BACK_URL:-}\"",
            "DOTNET=\"${DOTNET:-dotnet}\"",
            "command -v \"$DOTNET\" >/dev/null 2>&1 || { echo \"The .NET SDK is not available in the browser image.\" >&2; exit 1; }",
            "export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 QA_FRONT_URL QA_BACK_URL",
            "cd \"$QA_SOURCE\"",
            $"E2E_PROJECT=\"{project}\"",
            "\"$DOTNET\" build \"$E2E_PROJECT\" --configuration Release",
            "# playwright.ps1 is the documented installer of Playwright for .NET. The browser image carries PowerShell and the",
            "# browser's system libraries; only a browser revision the image lacks (the project's Playwright version differs",
            "# from the image's) is downloaded, into the image's world-writable browser folder.",
            script,
            "pwsh -NoProfile -File \"$PLAYWRIGHT_SCRIPT\" install chromium",
            "echo \">>> Browser tests against front $QA_FRONT_URL, back ${QA_BACK_URL:-none}\"",
            "RESULTS=\"$PWD/.e2e-results\"",
            "rm -rf \"$RESULTS\"",
            "STATUS=0",
            test,
            "# A run that left no result file proves nothing, whatever its exit code.",
            "test -n \"$(find \"$RESULTS\" -name '*.trx' -size +0 2>/dev/null | head -n 1)\" || { echo \"The browser tests left no result file ($RESULTS/*.trx): nothing was proved.\" >&2; exit 1; }",
            "exit \"$STATUS\"");
    }
}
