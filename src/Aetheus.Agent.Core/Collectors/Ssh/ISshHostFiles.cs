// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.Ssh;

/// <summary>The outcome of reading one file: its text, or why there is none.</summary>
internal sealed record SshFileRead(AuthorizedKeysFileState State, string? Content)
{
    public static readonly SshFileRead Absent = new(AuthorizedKeysFileState.Absent, null);
    public static readonly SshFileRead Unreadable = new(AuthorizedKeysFileState.Unreadable, null);
}

/// <summary>The few file-system reads the SSH collector needs, so the parser is tested without a host.</summary>
internal interface ISshHostFiles
{
    SshFileRead Read(string path);

    /// <summary>Files of <paramref name="directory"/> in ordinal order, or null when the directory
    /// exists but may not be listed. A missing directory lists as empty.</summary>
    IReadOnlyList<string>? List(string directory);

    /// <summary>Whether <paramref name="directory"/> is visible to the agent. Under a sandbox that hides or
    /// replaces the home directories (<c>ProtectHome=</c>) a user's <c>.ssh</c> is missing unless the
    /// installer bound it in: a missing file below it then says nothing about the user's keys.</summary>
    bool DirectoryExists(string directory);
}
