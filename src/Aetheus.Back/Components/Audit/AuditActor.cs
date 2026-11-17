// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Audit;

/// <summary>
/// R-7: the person an audit entry names when work done on their behalf runs after their request ended. The audit
/// reads the request's user; work moved off the request (the first stage dispatch of a manual launch, which starts
/// the child runs of a trigger step) has no request, and was audited as "system". That work's scope is given the
/// person captured in the request, so its entries name who clicked, as they did when the work ran inside the click.
/// It names the actor of the audit only: nothing authorizes against it (child launches are authorized against the
/// child pipeline's owner, F-EXEC-1b), so it can never lend a right.
/// </summary>
public interface IAuditActor
{
    /// <summary>The person this scope acts for, or null when it is the request's user (or nobody).</summary>
    string? Username { get; }

    /// <summary>Names the person this scope acts for. Set once: a scope acts for one person.</summary>
    void ActAs(string username);
}

public sealed class AuditActor : IAuditActor
{
    public string? Username { get; private set; }

    public void ActAs(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (Username is not null && !string.Equals(Username, username, StringComparison.Ordinal))
            throw new InvalidOperationException($"This scope already acts for '{Username}'; it cannot act for '{username}'.");
        Username = username;
    }
}
