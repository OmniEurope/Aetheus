// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>Refuses the ref and path values git would read as an option, before they reach an argv.</summary>
internal static class GitArgumentGuard
{
    // M-git-2: refs/paths flow from the query string into git argv. Argv is shell-safe (ArgumentList),
    // but a value starting with '-' can still be parsed by git as an OPTION (argument injection, e.g.
    // "--output=..."). git-check-ref-format forbids a leading '-' anyway, so rejecting it here is a
    // free defense-in-depth guard on every user-supplied ref/path before it reaches a git command.
    internal static void EnsureRefArgsSafe(params string?[] values)
    {
        foreach (var v in values)
        {
            if (v is not null && v.StartsWith('-'))
                throw new ArgumentException($"Ref or path may not start with '-': '{v}'.", nameof(values));
        }
    }
}
