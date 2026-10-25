// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Cli;

internal static class CliRuntime
{
    internal static Uri ParseServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "--server must be an absolute HTTP or HTTPS URL without embedded credentials.");
        }

        return uri;
    }

    internal static async Task<string?> ResolveTokenAsync(bool readFromStandardInput, CancellationToken ct)
    {
        var environmentToken = Environment.GetEnvironmentVariable("AETHEUS_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            if (readFromStandardInput)
                throw new ArgumentException("Choose either AETHEUS_TOKEN or --token-stdin, not both.");
            return environmentToken.Trim();
        }

        if (!readFromStandardInput)
            return null;
        if (!Console.IsInputRedirected)
            throw new ArgumentException("--token-stdin requires redirected standard input.");

        var token = (await Console.In.ReadLineAsync(ct))?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Standard input did not contain an authentication token.");
        return token;
    }

    internal static async Task<int> ExecuteAsync(
        Func<CancellationToken, Task<int>> action,
        CancellationToken ct)
    {
        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("Error: the server request timed out.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return maxLength <= 3 ? value[..maxLength] : value[..(maxLength - 3)] + "...";
    }
}
