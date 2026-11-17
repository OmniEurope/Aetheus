// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>
/// R-29: a probe failure stored as a stable reason code plus its parameters in <c>AppProbeResultDto.Error</c>
/// (<c>probe:code;host=h:port;status=500;expected=200</c>), never as a raw exception message. The front turns the code
/// into a localized sentence; rows written before this format keep their plain text and are shown as they are.
/// </summary>
public sealed record AppProbeFailure(string Code, string? Host, int? Status, int? Expected)
{
    public const string Prefix = "probe:";
    public const string ForbiddenTarget = "forbidden_target";
    public const string DnsFailure = "dns";
    public const string ConnectionRefused = "refused";
    public const string TimedOut = "timeout";
    public const string TlsFailure = "tls";
    public const string UnexpectedStatus = "status";
    public const string Unreachable = "unreachable";

    /// <summary>Every code the front knows how to word.</summary>
    public static readonly IReadOnlyList<string> KnownCodes =
        [ForbiddenTarget, DnsFailure, ConnectionRefused, TimedOut, TlsFailure, UnexpectedStatus, Unreachable];

    public string Encode()
    {
        var text = Prefix + Code;
        if (Host is not null)
            text += ";host=" + Host;
        if (Status is { } status)
            text += ";status=" + status.ToString(CultureInfo.InvariantCulture);
        if (Expected is { } expected)
            text += ";expected=" + expected.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    public static string ForStatus(string url, int status, int expected) =>
        new AppProbeFailure(UnexpectedStatus, HostOf(url), status, expected).Encode();

    public static string ForTimeout(string url) => new AppProbeFailure(TimedOut, HostOf(url), null, null).Encode();

    /// <summary>Classifies a failed request by exception type (never by message).</summary>
    public static string ForException(string url, Exception exception) =>
        new AppProbeFailure(CodeOf(exception), HostOf(url), null, null).Encode();

    /// <summary>The structured failure, or null for an old plain-text row (or anything that is not this format).</summary>
    public static AppProbeFailure? TryParse(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = text[Prefix.Length..].Split(';');
        if (!KnownCodes.Contains(parts[0]))
            return null;
        string? host = null;
        int? status = null, expected = null;
        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var value = part[(separator + 1)..];
            switch (part[..separator])
            {
                case "host": host = value; break;
                case "status": status = int.TryParse(value, CultureInfo.InvariantCulture, out var s) ? s : null; break;
                case "expected": expected = int.TryParse(value, CultureInfo.InvariantCulture, out var e) ? e : null; break;
            }
        }

        return new AppProbeFailure(parts[0], host, status, expected);
    }

    private static string CodeOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ProbeTargetForbiddenException:
                    return ForbiddenTarget;
                case AuthenticationException:
                    return TlsFailure;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }:
                    return DnsFailure;
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return ConnectionRefused;
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }:
                    return DnsFailure;
                case HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }:
                    return TlsFailure;
            }
        }

        return Unreachable;
    }

    private static string? HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
