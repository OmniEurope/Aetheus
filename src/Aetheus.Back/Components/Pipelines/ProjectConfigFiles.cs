// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The system configuration files the production deployment applies to a host, and the platform's generic
/// versions of them (ADR-057). A file is versioned with <c>#{NAME}#</c> placeholders and no value; the
/// platform only ever writes it into the project's configuration repository when it is missing, so a project
/// that edited it keeps its version.
/// </summary>
internal static class ProjectConfigFiles
{
    private const string ResourcePrefix = "ProjectConfigTemplates/apache/";

    public const string AppHttp = ".pipeline/configs/apache/app-http.conf";
    public const string AppHttps = ".pipeline/configs/apache/app-https.conf";
    public const string ApiHttp = ".pipeline/configs/apache/api-http.conf";
    public const string ApiHttps = ".pipeline/configs/apache/api-https.conf";

    /// <summary>The Apache virtual hosts the default deployment applies: the front's always, the API's only
    /// when the Compose file publishes a back port.</summary>
    /// <param name="includeApi">The Compose file publishes a back port: the API gets its own virtual hosts.</param>
    /// <param name="blueGreen">The HTTPS virtual hosts proxy to the ports of the upstream file the blue-green
    /// cutover maintains, instead of to the one port the Compose file publishes.</param>
    public static IReadOnlyDictionary<string, string> GenericApacheFiles(bool includeApi, bool blueGreen = false)
    {
        var suffix = blueGreen ? "-bluegreen" : string.Empty;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppHttp] = Embedded("app-http.conf"),
            [AppHttps] = Embedded($"app-https{suffix}.conf")
        };
        if (!includeApi) return files;
        files[ApiHttp] = Embedded("api-http.conf");
        files[ApiHttps] = Embedded($"api-https{suffix}.conf");
        return files;
    }

    private static string Embedded(string fileName)
    {
        var assembly = typeof(ProjectConfigFiles).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"The generic configuration template '{fileName}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
