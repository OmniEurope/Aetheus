// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

public class UfwStatusParserTests
{
    private const string Active = """
        Status: active

             To                         Action      From
             --                         ------      ----
        [ 1] 22/tcp                     ALLOW IN    Anywhere
        [ 2] 8080/tcp                   ALLOW IN    10.0.0.0/8
        [ 3] 80                         DENY IN     Anywhere
        """;

    [Fact]
    public void IsActive_True_WhenActive() => Assert.True(UfwStatusParser.IsActive(Active));

    [Fact]
    public void IsActive_False_WhenInactive() => Assert.False(UfwStatusParser.IsActive("Status: inactive"));

    [Fact]
    public void ParseRules_ExtractsPortProtoActionSource()
    {
        var rules = UfwStatusParser.ParseRules(Active);

        Assert.Equal(3, rules.Count);

        var ssh = rules[0];
        Assert.Equal(22, ssh.Port);
        Assert.Equal("tcp", ssh.Protocol);
        Assert.Equal("allow", ssh.Action);
        Assert.Equal("Anywhere", ssh.Source);

        var scoped = rules[1];
        Assert.Equal(8080, scoped.Port);
        Assert.Equal("10.0.0.0/8", scoped.Source);

        var deny = rules[2];
        Assert.Equal(80, deny.Port);
        Assert.Equal("any", deny.Protocol); // no /proto in "80"
        Assert.Equal("deny", deny.Action);
    }

    [Fact]
    public void ParseRules_Empty_ReturnsEmpty() => Assert.Empty(UfwStatusParser.ParseRules(""));

    [Fact]
    public void ParseRules_IncludesIpv6Rules()
    {
        // ufw status numbered emits a "(v6)" qualifier after the port for the IPv6 duplicate of a rule.
        const string withV6 = """
            Status: active

            [ 1] 22/tcp                     ALLOW IN    Anywhere
            [ 2] 22/tcp (v6)                ALLOW IN    Anywhere (v6)
            """;

        var rules = UfwStatusParser.ParseRules(withV6);

        Assert.Equal(2, rules.Count); // the IPv6 line must not be silently dropped
        Assert.All(rules, r => Assert.Equal(22, r.Port));
        Assert.All(rules, r => Assert.Equal("allow", r.Action));
    }
}
