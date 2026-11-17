// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Front.Tests;

/// <summary>
/// Asserts what an API client method really put on the wire: the exact HTTP method, the exact relative
/// URI (path and query) and, when there is one, the JSON body. A test that only checks the parsed
/// result would stay green with a wrong route, verb or body.
/// </summary>
internal static class SentRequestAssert
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Compares one captured request. <paramref name="expectedBody"/> is serialized the way the client
    /// serializes (web defaults) and compared structurally, so property order does not matter;
    /// <paramref name="expectedJson"/> is a raw JSON text compared the same way. With neither, the request
    /// must carry no body.
    /// </summary>
    public static void Matches(
        string actualMethod,
        string actualUrl,
        string? actualBody,
        HttpMethod method,
        string relativeUrl,
        object? expectedBody = null,
        string? expectedJson = null)
    {
        Assert.Equal(method.Method, actualMethod);
        Assert.Equal(new Uri(new Uri("http://test/"), relativeUrl).AbsoluteUri, new Uri(actualUrl).AbsoluteUri);

        if (expectedBody is null && expectedJson is null)
        {
            Assert.True(string.IsNullOrEmpty(actualBody), $"Expected no request body but got: {actualBody}");
            return;
        }

        Assert.False(string.IsNullOrEmpty(actualBody), "Expected a JSON request body but none was sent.");
        var expected = expectedJson is not null
            ? JsonNode.Parse(expectedJson)
            : JsonSerializer.SerializeToNode(expectedBody, expectedBody!.GetType(), Web);
        var actual = JsonNode.Parse(actualBody!);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Request body differs.{Environment.NewLine}Expected: {expected?.ToJsonString()}{Environment.NewLine}Actual:   {actual?.ToJsonString()}");
    }

    /// <summary>Asserts the last request recorded by the shared bUnit handler.</summary>
    internal static void LastSent(
        this BunitTestHelper.TestHandler handler,
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        string? json = null)
    {
        var (m, url, text) = handler.RequestDetails[^1];
        Matches(m, url, text, method, relativeUrl, body, json);
    }
}
