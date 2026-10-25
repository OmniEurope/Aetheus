// SPDX-License-Identifier: EUPL-1.2
// Test corpus for .aetheus/security-rules/opengrep/aetheus-csharp-injection.yml. Not compiled.
public class InjectionRuleCorpus : ControllerBase
{
    public void EntityFramework(AppDbContext db, string name)
    {
        // ruleid: aetheus.csharp.ef-raw-sql-dynamic
        db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'");
        // ruleid: aetheus.csharp.ef-raw-sql-dynamic
        db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = '" + name + "'");
        // ruleid: aetheus.csharp.ef-raw-sql-dynamic
        db.Database.SqlQueryRaw<int>("SELECT Id FROM Users WHERE Name = '" + name + "'");
        // ruleid: aetheus.csharp.ef-raw-sql-dynamic
        db.Database.ExecuteSqlRaw("DELETE FROM Users WHERE Name = '" + name + "'");
        // ruleid: aetheus.csharp.ef-raw-sql-dynamic
        db.Database.ExecuteSqlRawAsync("DELETE FROM Users WHERE Name = '" + name + "'");
        // ok: aetheus.csharp.ef-raw-sql-dynamic
        db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = {0}", name);
        // ok: aetheus.csharp.ef-raw-sql-dynamic
        db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Name = {name}");
        // ok: aetheus.csharp.ef-raw-sql-dynamic
        db.Users.FromSqlRaw("SELECT * FROM " + "Users");
        // ok: aetheus.csharp.ef-raw-sql-dynamic
        db.Database.ExecuteSqlRaw("DELETE FROM Users WHERE Name = {0}", name);
    }

    public void Ado(SqlConnection connection, NpgsqlConnection postgres, string name)
    {
        // ruleid: aetheus.csharp.ado-command-text-dynamic
        var one = new SqlCommand("SELECT * FROM Users WHERE Name = '" + name + "'", connection);
        // ruleid: aetheus.csharp.ado-command-text-dynamic
        var two = new NpgsqlCommand($"SELECT * FROM Users WHERE Name = '{name}'", postgres);
        // ruleid: aetheus.csharp.ado-command-text-dynamic
        one.CommandText = "DELETE FROM Users WHERE Name = '" + name + "'";
        // ok: aetheus.csharp.ado-command-text-dynamic
        var three = new SqlCommand("SELECT * FROM Users WHERE Name = @name", connection);
        // ok: aetheus.csharp.ado-command-text-dynamic
        three.CommandText = "SELECT 1";
        three.Parameters.AddWithValue("@name", name);
    }

    public void Dapper(SqlConnection connection, string name)
    {
        // ruleid: aetheus.csharp.dapper-dynamic-sql
        connection.Query($"SELECT * FROM Users WHERE Name = '{name}'");
        // ruleid: aetheus.csharp.dapper-dynamic-sql
        connection.ExecuteAsync("DELETE FROM Users WHERE Name = '" + name + "'");
        // ok: aetheus.csharp.dapper-dynamic-sql
        connection.Query("SELECT * FROM Users WHERE Name = @name", new { name });
    }

    public void ShellInterpreter(string folder)
    {
        // ruleid: aetheus.csharp.process-shell-interpreter-dynamic-arguments
        Process.Start("cmd.exe", "/c dir " + folder);
        // ruleid: aetheus.csharp.process-shell-interpreter-dynamic-arguments
        var info = new ProcessStartInfo("bash", $"-c \"ls {folder}\"");
        // ok: aetheus.csharp.process-shell-interpreter-dynamic-arguments
        Process.Start("bash", "-c \"ls /var/log\"");
        // ok: aetheus.csharp.process-shell-interpreter-dynamic-arguments
        var direct = new ProcessStartInfo("git", folder);
    }

    public void ProgramFromData(string tool, string argument)
    {
        // ruleid: aetheus.csharp.process-command-dynamic
        Process.Start("/usr/bin/" + tool);
        // ruleid: aetheus.csharp.process-command-dynamic
        var info = new ProcessStartInfo($"/usr/bin/{tool}");
        // ruleid: aetheus.csharp.process-command-dynamic
        info.Arguments = "--flag " + argument;
        // ok: aetheus.csharp.process-command-dynamic
        var safe = new ProcessStartInfo("git");
        safe.ArgumentList.Add(argument);
        // ok: aetheus.csharp.process-command-dynamic
        safe.Arguments = "status";
    }

    public IActionResult ReadFile([FromQuery] string fileName)
    {
        // ruleid: aetheus.csharp.path-traversal-request-input
        var content = System.IO.File.ReadAllText(Path.Combine("/srv/files", fileName));
        return Ok(content);
    }

    public IActionResult ReadFileFromRequest()
    {
        // ruleid: aetheus.csharp.path-traversal-request-input
        var bytes = System.IO.File.ReadAllBytes("/srv/files/" + Request.Query["name"]);
        return Ok(bytes);
    }

    public IActionResult ReadFileSafely([FromQuery] string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        // ok: aetheus.csharp.path-traversal-request-input
        var content = System.IO.File.ReadAllText(Path.Combine("/srv/files", safeName));
        return Ok(content);
    }

    public IActionResult ReadConstantFile()
    {
        // ok: aetheus.csharp.path-traversal-request-input
        var content = System.IO.File.ReadAllText("/srv/files/readme.txt");
        return Ok(content);
    }

    public async Task<IActionResult> Fetch([FromQuery] string url, HttpClient client)
    {
        // ruleid: aetheus.csharp.ssrf-request-input
        var body = await client.GetStringAsync(url);
        return Ok(body);
    }

    public async Task<IActionResult> FetchChecked([FromQuery] string url, HttpClient client)
    {
        // ok: aetheus.csharp.ssrf-request-input
        var body = await client.GetStringAsync("https://api.example.org/status");
        return Ok(body);
    }

    public void XPath(XmlDocument document, string user)
    {
        // ruleid: aetheus.csharp.xpath-injection
        document.SelectSingleNode("//user[@name='" + user + "']");
        // ruleid: aetheus.csharp.xpath-injection
        document.SelectNodes($"//user[@name='{user}']");
        // ok: aetheus.csharp.xpath-injection
        document.SelectNodes("//user");
    }

    public void Ldap(DirectoryEntry entry, string user)
    {
        // ruleid: aetheus.csharp.ldap-filter-injection
        var searcher = new DirectorySearcher(entry, "(uid=" + user + ")");
        // ruleid: aetheus.csharp.ldap-filter-injection
        searcher.Filter = $"(uid={user})";
        // ok: aetheus.csharp.ldap-filter-injection
        var constant = new DirectorySearcher(entry, "(objectClass=user)");
    }

    public void Markup(IHtmlHelper html, string comment)
    {
        // ruleid: aetheus.csharp.html-raw-dynamic
        html.Raw(comment);
        // ruleid: aetheus.csharp.html-raw-dynamic
        var fragment = new HtmlString(comment);
        // ok: aetheus.csharp.html-raw-dynamic
        html.Raw("<hr />");
        // ok: aetheus.csharp.html-raw-dynamic
        var encoded = new HtmlString(HtmlEncoder.Default.Encode(comment));
    }

    public IActionResult Page(string comment)
    {
        // ruleid: aetheus.csharp.html-raw-dynamic
        return Content(comment, "text/html");
    }

    public IActionResult PlainText(string comment)
    {
        // ok: aetheus.csharp.html-raw-dynamic
        return Content(comment, "text/plain");
    }

    public void Expressions(string pattern, string input)
    {
        // ruleid: aetheus.csharp.regex-dynamic-pattern-without-timeout
        var one = new Regex(pattern);
        // ruleid: aetheus.csharp.regex-dynamic-pattern-without-timeout
        Regex.IsMatch(input, pattern);
        // ruleid: aetheus.csharp.regex-dynamic-pattern-without-timeout
        Regex.Replace(input, pattern, "x", RegexOptions.IgnoreCase);
        // ok: aetheus.csharp.regex-dynamic-pattern-without-timeout
        var two = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        // ok: aetheus.csharp.regex-dynamic-pattern-without-timeout
        var three = new Regex(Regex.Escape(pattern));
        // ok: aetheus.csharp.regex-dynamic-pattern-without-timeout
        var four = new Regex("^[a-z]+$");
        // ok: aetheus.csharp.regex-dynamic-pattern-without-timeout
        Regex.IsMatch(input, "^[0-9]+$");
        // ok: aetheus.csharp.regex-dynamic-pattern-without-timeout
        Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
