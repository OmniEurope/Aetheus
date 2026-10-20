// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Shared;

/// <summary> A multi-valued column filter reaches the API as one query key repeated per value
/// (<c>statuses=Open&amp;statuses=Fixed</c>), which ASP.NET binds to a list parameter. </summary>
public static class QueryValues
{
    /// <summary>Appends <paramref name="key"/> once per value; nothing when there is none.</summary>
    public static string AddMany<T>(string url, string key, IEnumerable<T>? values)
    {
        if (values is null) return url;
        foreach (var value in values)
        {
            var text = value?.ToString();
            if (!string.IsNullOrEmpty(text))
                url = QueryHelpers.AddQueryString(url, key, text);
        }

        return url;
    }
}
