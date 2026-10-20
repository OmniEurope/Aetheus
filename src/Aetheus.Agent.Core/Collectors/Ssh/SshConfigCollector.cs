// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.Ssh;

/// <summary>PLAN-010 lot 1: the effective sshd settings that matter and the fingerprints of the
/// authorized keys, read with the agent's own rights (no sudo, no new grant).</summary>
public interface ISshConfigCollector
{
    Task<SshConfigDataDto> CollectAsync(CancellationToken ct = default);
}

public sealed class SshConfigCollector : ISshConfigCollector
{
    private readonly ISshHostFiles _files;
    private readonly bool _isWindows;

    public SshConfigCollector() : this(new SshHostFiles(), OperatingSystem.IsWindows()) { }

    internal SshConfigCollector(ISshHostFiles files, bool isWindows)
    {
        _files = files;
        _isWindows = isWindows;
    }

    public Task<SshConfigDataDto> CollectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_isWindows || _files.Read(SshdConfigParser.MainConfigPath).State == AuthorizedKeysFileState.Absent)
            return Task.FromResult(new SshConfigDataDto { Collected = false });

        var config = new SshdConfigParser(_files).Parse();
        var keys = new AuthorizedKeysReader(_files).Read(config.AuthorizedKeysFile.Value);
        return Task.FromResult(new SshConfigDataDto
        {
            Collected = true,
            PermitRootLogin = config.PermitRootLogin,
            PasswordAuthentication = config.PasswordAuthentication,
            AuthorizedKeysFile = config.AuthorizedKeysFile,
            UnreadableFiles = config.UnreadableFiles,
            AuthorizedKeys = keys
        });
    }
}
