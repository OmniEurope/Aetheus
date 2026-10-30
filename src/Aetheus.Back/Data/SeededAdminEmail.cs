// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Data;

/// <summary>The sign-in e-mail of the administrator the seed creates (PLAN-003 P3-2: people sign in by
/// e-mail). <c>Auth:AdminEmail</c> names it; a new install without it gets <see cref="Default"/>.</summary>
internal static class SeededAdminEmail
{
    internal const string SeededUsername = "admin";
    internal const string Default = "admin@aetheus.local";

    internal static string Resolve(IConfiguration? config) =>
        UserEmail.Normalize(config?["Auth:AdminEmail"]) ?? Default;
}
