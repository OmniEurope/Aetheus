// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PersonalAccessTokens;

/// <summary>The column filters of the caller's personal access tokens grid.</summary>
internal static class PersonalAccessTokenListQuery
{
    internal static readonly GridQueryMap<PersonalAccessToken> Columns = new GridQueryMap<PersonalAccessToken>()
        .Text("name", t => t.Name)
        .Text("tokenPrefix", t => t.TokenPrefix)
        .Enum("scope", t => t.Scope)
        .Date("createdAt", t => t.CreatedAt)
        .Date("expiresAt", t => t.ExpiresAt)
        .Date("lastUsedAt", t => t.LastUsedAt);
}
