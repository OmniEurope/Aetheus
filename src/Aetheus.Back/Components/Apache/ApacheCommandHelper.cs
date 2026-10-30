// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Apache;

public static partial class ApacheCommandHelper
{
    public static bool IsValidSiteName(string name) =>
        !string.IsNullOrWhiteSpace(name) && SiteNameRegex().IsMatch(name);

    public static string BuildServiceCommand(ApacheAction action) => action switch
    {
        ApacheAction.Start => "systemctl start apache2 || systemctl start httpd",
        ApacheAction.Stop => "systemctl stop apache2 || systemctl stop httpd",
        ApacheAction.Restart => "systemctl restart apache2 || systemctl restart httpd",
        ApacheAction.Reload => "systemctl reload apache2 || systemctl reload httpd",
        ApacheAction.TestConfig => "apache2ctl configtest 2>&1 || apachectl configtest 2>&1 || httpd -t 2>&1",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    // BuildEnableSiteCommand / BuildDisableSiteCommand were removed (recette R-12): the agent's
    // CommandValidator refused their `a2ensite x && systemctl reload` strings. Enable, disable and delete
    // a site now dispatch typed OperationKind.ApacheEnableSite / ApacheDisableSite / ApacheDeleteSite.

    // BuildEnableModuleCommand / BuildDisableModuleCommand were removed (recette R-119): their
    // `a2enmod x && systemctl restart apache2` strings ran without root and carried no operation kind.
    // Module changes now dispatch typed OperationKind.ApacheEnableModule / ApacheDisableModule.

    public static string BuildGetLogsCommand(string logType, int lines)
    {
        var logFile = logType == "access"
            ? "/var/log/apache2/access.log"
            : "/var/log/apache2/error.log";
        var altLogFile = logType == "access"
            ? "/var/log/httpd/access_log"
            : "/var/log/httpd/error_log";

        return $"tail -n {lines} {logFile} 2>/dev/null || tail -n {lines} {altLogFile} 2>/dev/null";
    }

    // BuildGetConfigCommand / BuildGetHtaccessCommand / BuildSaveHtaccessCommand were removed: those
    // shell builders (cat "..." || cat, printf | base64 -d > ...) carry metacharacters the agent's
    // CommandValidator rejects, so the tasks died silently. GetVHostConfig / GetHtaccess / SaveHtaccess
    // now dispatch typed OperationKind.ApacheGetConfig / ApacheGetHtaccess / ApacheSaveHtaccess handled
    // by ApacheOperationExecutor (direct File read/write, no shell).

    /// <summary>
    /// An absolute Linux path with no traversal. The regex alone cannot express this: it allows both
    /// '.' and '/', so "/var/www/../../etc" matches it while escaping the intended root. The segment
    /// test is what makes the name true.
    /// </summary>
    public static bool IsValidDocumentRoot(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && DocumentRootRegex().IsMatch(path)
        && !path.Split('/').Contains("..");

    // Only allow alphanumeric, hyphens, dots, and .conf extension
    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SiteNameRegex();

    // Absolute Linux path: letters, digits, hyphens, underscores, dots, slashes
    [GeneratedRegex(@"^/[a-zA-Z0-9._/-]+$")]
    private static partial Regex DocumentRootRegex();
}
