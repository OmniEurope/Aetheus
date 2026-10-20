// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Agent.Core.Collectors.Ssh;

/// <summary>
/// Fingerprints the authorized_keys files of root and of every login user listed in
/// <c>/etc/passwd</c>, at the places the effective <c>AuthorizedKeysFile</c> names. Only what the
/// agent may read with its own rights is read; anything else is reported unreadable.
/// </summary>
internal sealed class AuthorizedKeysReader(ISshHostFiles files)
{
    private const string PasswdPath = "/etc/passwd";
    private const int MaxUsers = 32;
    private const string DefaultAuthorizedKeysFile = ".ssh/authorized_keys .ssh/authorized_keys2";

    public List<AuthorizedKeysFileDto> Read(string? authorizedKeysFile)
    {
        var result = new List<AuthorizedKeysFileDto>();
        var patterns = (authorizedKeysFile ?? DefaultAuthorizedKeysFile)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (user, home) in LoginUsers())
        {
            foreach (var pattern in patterns)
            {
                var path = Expand(pattern, user, home);
                if (!seen.Add(path)) continue;
                result.Add(ReadFile(user, home, path));
            }
        }
        return result;
    }

    private AuthorizedKeysFileDto ReadFile(string user, string home, string path)
    {
        var read = files.Read(path);
        var state = read.State;
        // The installer binds only the .ssh directories that existed at install time into a sandbox whose
        // homes are otherwise an empty tmpfs: below a home, "no such file" next to a directory that is
        // not there means "not shown to the agent", not "this user has no keys" (a .ssh created since
        // would be missed). Say unknown rather than none; "no such file" counts only where the directory
        // that would hold it is visible.
        if (state == AuthorizedKeysFileState.Absent && IsUnder(path, home) && !files.DirectoryExists(ParentOf(path)))
            state = AuthorizedKeysFileState.Unreadable;
        return new AuthorizedKeysFileDto
        {
            User = user,
            Path = path,
            State = state,
            Keys = state == AuthorizedKeysFileState.Read ? ParseKeys(read.Content!) : []
        };
    }

    // Linux paths: Path.GetDirectoryName would answer with backslashes where the tests run on Windows.
    private static string ParentOf(string path) => path[..Math.Max(path.LastIndexOf('/'), 1)];

    private static bool IsUnder(string path, string home) =>
        path.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>Root first, then the users with a login shell and a regular uid.</summary>
    internal IEnumerable<(string User, string Home)> LoginUsers()
    {
        var users = new List<(string, string)> { ("root", "/root") };
        var passwd = files.Read(PasswdPath);
        if (passwd.State != AuthorizedKeysFileState.Read) return users;
        foreach (var line in passwd.Content!.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length < 7 || fields[0] == "root") continue;
            if (!int.TryParse(fields[2], out var uid) || uid < 1000 || uid == 65534) continue;
            var shell = fields[6];
            if (shell.EndsWith("nologin", StringComparison.Ordinal) || shell.EndsWith("/false", StringComparison.Ordinal))
                continue;
            users.Add((fields[0], fields[5]));
            if (users.Count >= MaxUsers) break;
        }
        return users;
    }

    /// <summary>The OpenSSH tokens %h, %u and %%; a relative path is taken from the home directory.</summary>
    internal static string Expand(string pattern, string user, string home)
    {
        var expanded = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%' || i + 1 >= pattern.Length)
            {
                expanded.Append(pattern[i]);
                continue;
            }
            var token = pattern[++i];
            expanded.Append(token switch
            {
                'h' => home,
                'u' => user,
                '%' => "%",
                _ => "%" + token
            });
        }
        var path = expanded.ToString();
        return path.StartsWith('/') ? path : $"{home.TrimEnd('/')}/{path}";
    }

    internal static List<AuthorizedKeyDto> ParseKeys(string content)
    {
        var keys = new List<AuthorizedKeyDto>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            keys.Add(ParseLine(line));
        }
        return keys;
    }

    private static AuthorizedKeyDto ParseLine(string line)
    {
        var tokens = SplitOutsideQuotes(line);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!IsKeyType(tokens[i]) || !TryDecode(tokens[i + 1], out var blob)) continue;
            return new AuthorizedKeyDto
            {
                KeyType = tokens[i],
                Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')
            };
        }
        // Not a key sshd would accept as written; still watched, through the hash of the line.
        return new AuthorizedKeyDto
        {
            KeyType = "unparsed",
            Fingerprint = "LINE-SHA256:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(line))).TrimEnd('=')
        };
    }

    private static bool IsKeyType(string token) =>
        token.StartsWith("ssh-", StringComparison.Ordinal)
        || token.StartsWith("ecdsa-sha2-", StringComparison.Ordinal)
        || token.StartsWith("sk-ssh-", StringComparison.Ordinal)
        || token.StartsWith("sk-ecdsa-", StringComparison.Ordinal);

    private static bool TryDecode(string token, out byte[] blob)
    {
        var buffer = new byte[token.Length];
        if (!Convert.TryFromBase64String(token, buffer, out var written) || written == 0)
        {
            blob = [];
            return false;
        }
        blob = buffer[..written];
        return true;
    }

    /// <summary>Options may hold quoted values with spaces (<c>command="a b"</c>).</summary>
    private static List<string> SplitOutsideQuotes(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
