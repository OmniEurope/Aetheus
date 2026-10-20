// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors.Ssh;

namespace Aetheus.Agent.Core.Tests;

public sealed class SshConfigCollectorTests
{
    private const string Ed25519Line =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINb/i1txsnmQ/fkT6TnV5RptMr252c5Kidwy8X5Vls50 alice@laptop";
    // A throwaway public key; `ssh-keygen -lf` (OpenSSH 9.6p1) printed this fingerprint for it.
    private const string Ed25519Fingerprint = "SHA256:1csW3TyV5xev7+O26Pde3Gi1GwikW8ltg+3vZdMpZ70";

    private sealed class FakeFiles : ISshHostFiles
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

        public SshFileRead Read(string path)
        {
            if (Denied.Contains(path) || Denied.Any(d => path.StartsWith(d + "/", StringComparison.Ordinal)))
                return SshFileRead.Unreadable;
            return Files.TryGetValue(path, out var content)
                ? new SshFileRead(AuthorizedKeysFileState.Read, content)
                : SshFileRead.Absent;
        }

        /// <summary>Directories the sandbox does not show (an unbound home .ssh); every other one exists.</summary>
        public HashSet<string> HiddenDirectories { get; } = new(StringComparer.Ordinal);

        public bool DirectoryExists(string directory) => !HiddenDirectories.Contains(directory);

        public IReadOnlyList<string>? List(string directory)
        {
            if (Denied.Contains(directory)) return null;
            return Files.Keys
                .Where(k => k.StartsWith(directory + "/", StringComparison.Ordinal)
                    && !k[(directory.Length + 1)..].Contains('/'))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }

    private static SshdConfigResult Parse(FakeFiles files) => new SshdConfigParser(files).Parse();

    [Fact]
    public void NoSetting_ReportsTheOpenSshDefaults()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "# nothing set\nPort 22\n";

        var result = Parse(files);

        Assert.Equal("prohibit-password", result.PermitRootLogin.Value);
        Assert.Equal("default", result.PermitRootLogin.Source);
        Assert.Equal("yes", result.PasswordAuthentication.Value);
        Assert.Equal(".ssh/authorized_keys .ssh/authorized_keys2", result.AuthorizedKeysFile.Value);
        Assert.Empty(result.UnreadableFiles);
    }

    [Fact]
    public void FirstValueWins_AcrossIncludes_InLexicalOrder()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] =
            "Include /etc/ssh/sshd_config.d/*.conf\nPermitRootLogin yes\nPasswordAuthentication yes\n";
        files.Files["/etc/ssh/sshd_config.d/50-cloud.conf"] = "PasswordAuthentication no\n";
        files.Files["/etc/ssh/sshd_config.d/10-hard.conf"] = "PermitRootLogin no\nPasswordAuthentication yes\n";
        files.Files["/etc/ssh/sshd_config.d/README"] = "PermitRootLogin forced-commands-only\n";

        var result = Parse(files);

        Assert.Equal("no", result.PermitRootLogin.Value);
        Assert.Equal("/etc/ssh/sshd_config.d/10-hard.conf:1", result.PermitRootLogin.Source);
        // 10-hard.conf comes before 50-cloud.conf: its "yes" is the first value.
        Assert.Equal("yes", result.PasswordAuthentication.Value);
        Assert.Equal("/etc/ssh/sshd_config.d/10-hard.conf:2", result.PasswordAuthentication.Source);
    }

    [Fact]
    public void RelativeInclude_IsTakenFromEtcSsh()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "Include extra.conf\n";
        files.Files["/etc/ssh/extra.conf"] = "PermitRootLogin=yes\n";

        Assert.Equal("yes", Parse(files).PermitRootLogin.Value);
    }

    [Fact]
    public void Comments_EqualsSign_CaseAndQuotes_AreReadLikeOpenSsh()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] =
            "#PermitRootLogin yes\n  # PasswordAuthentication yes\r\n"
            + "permitrootlogin=WITHOUT-PASSWORD # trailing comment\n"
            + "AuthorizedKeysFile \"/etc/ssh/keys/%u\" .ssh/authorized_keys\n"
            + "PASSWORDAUTHENTICATION No\n";

        var result = Parse(files);

        Assert.Equal("prohibit-password", result.PermitRootLogin.Value);
        Assert.Equal("/etc/ssh/sshd_config:3", result.PermitRootLogin.Source);
        Assert.Equal("no", result.PasswordAuthentication.Value);
        Assert.Equal("/etc/ssh/keys/%u .ssh/authorized_keys", result.AuthorizedKeysFile.Value);
    }

    [Fact]
    public void MatchBlock_IsNotTheGlobalValue_ButIsFlagged()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] =
            "Match User deploy\n  PermitRootLogin yes\nMatch all\nPasswordAuthentication no\n";

        var result = Parse(files);

        Assert.Equal("prohibit-password", result.PermitRootLogin.Value);
        Assert.True(result.PermitRootLogin.OverriddenInMatch);
        Assert.Equal("no", result.PasswordAuthentication.Value);
        Assert.False(result.PasswordAuthentication.OverriddenInMatch);
    }

    [Fact]
    public void MatchInIncludedFile_EndsWithThatFile()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "Include /etc/ssh/sshd_config.d/*.conf\nPermitRootLogin yes\n";
        files.Files["/etc/ssh/sshd_config.d/a.conf"] = "Match Address 10.0.0.0/8\nPasswordAuthentication yes\n";

        var result = Parse(files);

        Assert.Equal("yes", result.PermitRootLogin.Value);
        Assert.Equal("/etc/ssh/sshd_config:2", result.PermitRootLogin.Source);
        Assert.True(result.PasswordAuthentication.OverriddenInMatch);
    }

    [Fact]
    public void FileIncludedFromMatch_StaysConditional_EvenWithMatchAll()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "Match User x\nInclude /etc/ssh/inner.conf\n";
        files.Files["/etc/ssh/inner.conf"] = "Match all\nPermitRootLogin yes\n";

        var result = Parse(files);

        Assert.Equal("prohibit-password", result.PermitRootLogin.Value);
        Assert.True(result.PermitRootLogin.OverriddenInMatch);
    }

    [Fact]
    public void UnreadableIncludeBeforeTheValue_MakesItUnknown_NeverTheDefault()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] =
            "PasswordAuthentication no\nInclude /etc/ssh/sshd_config.d/*.conf\nPermitRootLogin no\n";
        files.Files["/etc/ssh/sshd_config.d/50-cloud-init.conf"] = "PermitRootLogin yes\n";
        files.Denied.Add("/etc/ssh/sshd_config.d/50-cloud-init.conf");

        var result = Parse(files);

        // Set before the unreadable file: known.
        Assert.Equal("no", result.PasswordAuthentication.Value);
        // Set after it: the hidden file may have set it first.
        Assert.Null(result.PermitRootLogin.Value);
        Assert.Null(result.PermitRootLogin.Source);
        // Never set anywhere readable: not the default either.
        Assert.Null(result.AuthorizedKeysFile.Value);
        Assert.Equal(["/etc/ssh/sshd_config.d/50-cloud-init.conf"], result.UnreadableFiles);
    }

    [Fact]
    public void UnlistableIncludeDirectory_IsReportedUnreadable()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "Include /etc/ssh/sshd_config.d/*.conf\n";
        files.Denied.Add("/etc/ssh/sshd_config.d");

        var result = Parse(files);

        Assert.Null(result.PermitRootLogin.Value);
        Assert.Equal(["/etc/ssh/sshd_config.d/*.conf"], result.UnreadableFiles);
    }

    [Fact]
    public void GlobWithoutMatch_AndMissingLiteralInclude_AreNotErrors()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "Include /etc/ssh/sshd_config.d/*.conf\nInclude /etc/ssh/none.conf\n";

        var result = Parse(files);

        Assert.Equal("prohibit-password", result.PermitRootLogin.Value);
        Assert.Empty(result.UnreadableFiles);
    }

    [Fact]
    public async Task Collect_OnWindows_OrWithoutSshd_IsNotCollected()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = new FakeFiles();
        Assert.False((await new SshConfigCollector(files, isWindows: false).CollectAsync(ct)).Collected);
        files.Files["/etc/ssh/sshd_config"] = "";
        Assert.False((await new SshConfigCollector(files, isWindows: true).CollectAsync(ct)).Collected);
    }

    [Fact]
    public async Task Collect_FingerprintsKeys_AndReportsRootUnreadable()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "PermitRootLogin no\n";
        files.Files["/etc/passwd"] =
            "root:x:0:0:root:/root:/bin/bash\n"
            + "daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\n"
            + "alice:x:1000:1000::/home/alice:/bin/bash\n"
            + "aetheus:x:999:999::/opt/aetheus:/usr/sbin/nologin\n"
            + "nobody:x:65534:65534::/nonexistent:/bin/sh\n";
        files.Files["/root/.ssh/authorized_keys"] = Ed25519Line;
        files.Denied.Add("/root");
        files.Files["/home/alice/.ssh/authorized_keys"] =
            "# comment\n\n" + Ed25519Line + "\n"
            + "command=\"echo a b\",no-pty " + Ed25519Line + "\n"
            + "garbage line\n";

        var data = await new SshConfigCollector(files, isWindows: false).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.Collected);
        Assert.Equal(
            ["/root/.ssh/authorized_keys", "/root/.ssh/authorized_keys2", "/home/alice/.ssh/authorized_keys", "/home/alice/.ssh/authorized_keys2"],
            data.AuthorizedKeys.Select(k => k.Path));
        Assert.All(data.AuthorizedKeys.Where(k => k.User == "root"), k => Assert.Equal(AuthorizedKeysFileState.Unreadable, k.State));
        var alice = data.AuthorizedKeys[2];
        Assert.Equal(AuthorizedKeysFileState.Read, alice.State);
        Assert.Equal(3, alice.Keys.Count);
        Assert.Equal(Ed25519Fingerprint, alice.Keys[0].Fingerprint);
        Assert.Equal("ssh-ed25519", alice.Keys[0].KeyType);
        Assert.Equal(Ed25519Fingerprint, alice.Keys[1].Fingerprint);
        Assert.Equal("unparsed", alice.Keys[2].KeyType);
        Assert.StartsWith("LINE-SHA256:", alice.Keys[2].Fingerprint, StringComparison.Ordinal);
        Assert.Equal(AuthorizedKeysFileState.Absent, data.AuthorizedKeys[3].State);
        // The raw key never leaves the host.
        Assert.DoesNotContain(data.AuthorizedKeys.SelectMany(f => f.Keys), k => k.Fingerprint.Contains("AAAAC3Nza", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sandbox shows a home's .ssh only when the installer bound it in. A key file missing next to a
    /// directory that is not visible is unknown (a .ssh created since the install would otherwise read as
    /// "no keys"); next to a visible directory, or outside the home, "no such file" still means none.
    /// </summary>
    [Fact]
    public async Task Collect_MissingFileBesideAnInvisibleSshDirectory_IsUnknown_NeverNone()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "AuthorizedKeysFile .ssh/authorized_keys /etc/ssh/keys/%u\n";
        files.Files["/etc/passwd"] =
            "root:x:0:0:root:/root:/bin/bash\n"
            + "alice:x:1000:1000::/home/alice:/bin/bash\n";
        files.Files["/root/.ssh/authorized_keys"] = Ed25519Line;
        files.HiddenDirectories.Add("/home/alice/.ssh");
        files.HiddenDirectories.Add("/etc/ssh/keys");

        var data = await new SshConfigCollector(files, isWindows: false).CollectAsync(TestContext.Current.CancellationToken);

        var byPath = data.AuthorizedKeys.ToDictionary(k => k.Path, k => k.State, StringComparer.Ordinal);
        Assert.Equal(AuthorizedKeysFileState.Read, byPath["/root/.ssh/authorized_keys"]);
        Assert.Equal(AuthorizedKeysFileState.Unreadable, byPath["/home/alice/.ssh/authorized_keys"]);
        // Outside the home the file system is not sandboxed away: a missing file is a missing file.
        Assert.Equal(AuthorizedKeysFileState.Absent, byPath["/etc/ssh/keys/alice"]);
    }

    [Fact]
    public async Task Collect_BoundSshDirectoryWithoutTheFile_IsNone()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "# defaults\n";
        files.Files["/etc/passwd"] = "alice:x:1000:1000::/home/alice:/bin/bash\n";
        files.Files["/root/.ssh/authorized_keys"] = Ed25519Line;

        var data = await new SshConfigCollector(files, isWindows: false).CollectAsync(TestContext.Current.CancellationToken);

        var keys2 = data.AuthorizedKeys.Single(k => k.Path == "/home/alice/.ssh/authorized_keys2");
        Assert.Equal(AuthorizedKeysFileState.Absent, keys2.State);
    }

    [Fact]
    public async Task Collect_FollowsTheConfiguredAuthorizedKeysFile()
    {
        var files = new FakeFiles();
        files.Files["/etc/ssh/sshd_config"] = "AuthorizedKeysFile /etc/ssh/keys/%u %h/.ssh/k%%\n";
        files.Files["/etc/ssh/keys/root"] = Ed25519Line;

        var data = await new SshConfigCollector(files, isWindows: false).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["/etc/ssh/keys/root", "/root/.ssh/k%"], data.AuthorizedKeys.Select(k => k.Path));
        Assert.Equal(Ed25519Fingerprint, Assert.Single(data.AuthorizedKeys[0].Keys).Fingerprint);
    }

    [Theory]
    [InlineData("PermitRootLogin yes", new[] { "PermitRootLogin", "yes" })]
    [InlineData("  Key=value  ", new[] { "Key", "value" })]
    [InlineData("Key = value", new[] { "Key", "value" })]
    [InlineData("Banner \"/etc/my banner\"", new[] { "Banner", "/etc/my banner" })]
    [InlineData("Key value#notacomment", new[] { "Key", "value#notacomment" })]
    [InlineData("# PermitRootLogin yes", new string[0])]
    public void Tokenize_FollowsOpenSshRules(string line, string[] expected)
    {
        Assert.Equal(expected, SshdConfigParser.Tokenize(line));
    }
}
