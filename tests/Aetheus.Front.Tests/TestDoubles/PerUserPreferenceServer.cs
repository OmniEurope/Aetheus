// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text;
using System.Text.Json;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// The <c>api/users/me/preferences</c> endpoints of a test (R1-66): one row set per user, the user being the
/// one the browser is signed in as when the request leaves, as the backend reads it from the token. Every
/// other request goes to the inner handler. The backend's own isolation is proven against PostgreSQL by
/// the integration tests; this double lets a front test watch what each account receives.
/// </summary>
internal sealed class PerUserPreferenceServer(Func<int?> currentUser, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string Root = "/api/users/me/preferences";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<int, Dictionary<string, UserPreferenceDto>> _rows = [];

    /// <summary>Every preference request, with the user it was answered for.</summary>
    public List<(int? User, string Method, string Path)> Requests { get; } = [];

    /// <summary>Stores a value for <paramref name="userId"/> as the server would hold it.</summary>
    public void Seed<T>(int userId, string key, T value) =>
        Rows(userId)[key] = new UserPreferenceDto { Key = key, Value = JsonSerializer.Serialize(value, JsonOptions), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };

    /// <summary>The raw JSON value <paramref name="userId"/> holds for <paramref name="key"/>, or null.</summary>
    public string? Value(int userId, string key) => Rows(userId).TryGetValue(key, out var row) ? row.Value : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (!path.StartsWith(Root, StringComparison.Ordinal))
            return await base.SendAsync(request, cancellationToken);

        var user = currentUser();
        Requests.Add((user, request.Method.Method, path));
        if (user is not { } userId)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var rows = Rows(userId);
        var key = path.Length > Root.Length ? Uri.UnescapeDataString(path[(Root.Length + 1)..]) : null;
        if (key is null)
            return Json(rows.Values.ToList());
        if (request.Method == HttpMethod.Get)
            return rows.TryGetValue(key, out var row) ? Json(row) : new HttpResponseMessage(HttpStatusCode.NoContent);

        var saved = JsonSerializer.Deserialize<SaveUserPreferenceRequest>(
            await request.Content!.ReadAsStringAsync(cancellationToken), JsonOptions)!;
        rows[key] = new UserPreferenceDto { Key = key, Value = saved.Value, UpdatedAt = saved.UpdatedAt };
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private Dictionary<string, UserPreferenceDto> Rows(int userId)
    {
        if (!_rows.TryGetValue(userId, out var rows))
            _rows[userId] = rows = new Dictionary<string, UserPreferenceDto>(StringComparer.Ordinal);
        return rows;
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
    };
}
