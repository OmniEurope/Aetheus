// SPDX-License-Identifier: EUPL-1.2
namespace RehearsalBlazor;

/// <summary>The release version the deployment hands the application (APP_VERSION), shown on the page and at /version.</summary>
public static class VersionInfo
{
    public static string Current { get; } = Environment.GetEnvironmentVariable("APP_VERSION") is { Length: > 0 } version ? version : "unknown";
}
