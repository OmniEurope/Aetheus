// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Aetheus.Agent.Core.Services;

/// <summary>Identify the host from Linux machine-id paths or Windows MachineGuid so re-enrollment
/// can revive its server. Send only application-keyed HMAC-SHA256 as lowercase hex; the raw
/// identity stays on the host.</summary>
public static class MachineIdentity
{
    internal const string LinuxMachineIdPath = "/etc/machine-id";
    internal const string LinuxDbusMachineIdPath = "/var/lib/dbus/machine-id";

    // Fixed forever: changing it changes every host's hash, and no reinstalled machine would be
    // recognised any more.
    private static readonly byte[] ApplicationKey = Encoding.UTF8.GetBytes("Aetheus.MachineIdentity.v1");

    /// <summary>Reads this host's identity and returns its hash, or null when the host exposes none.</summary>
    public static string? ReadHash() => OperatingSystem.IsWindows()
        ? ComputeHash(isWindows: true, ReadFileOrNull, ReadWindowsMachineGuid)
        : ComputeHash(isWindows: false, ReadFileOrNull, static () => null);

    /// <summary>Pure core of <see cref="ReadHash"/>, with the file and registry readers injected.</summary>
    internal static string? ComputeHash(bool isWindows, Func<string, string?> readFile, Func<string?> readMachineGuid)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        ArgumentNullException.ThrowIfNull(readMachineGuid);
        var raw = isWindows
            ? readMachineGuid()
            : FirstNonBlank(readFile(LinuxMachineIdPath), () => readFile(LinuxDbusMachineIdPath));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var normalized = raw.Trim().ToLowerInvariant();
        var hash = HMACSHA256.HashData(ApplicationKey, Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    private static string? FirstNonBlank(string? first, Func<string?> fallback) =>
        string.IsNullOrWhiteSpace(first) ? fallback() : first;

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            // 64-bit view explicitly: a 32-bit view is redirected to WOW6432Node, which has no MachineGuid.
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
