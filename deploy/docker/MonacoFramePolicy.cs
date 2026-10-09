// SPDX-License-Identifier: EUPL-1.2

internal static class MonacoFramePolicy
{
    internal const string Path = "/monaco-frame.html";
    internal const string OmniPath = "/omni-monaco-frame.html";

    internal static string Build(string? reportUri) =>
        "default-src 'self'; " +
        "script-src 'self' blob:; " +
        "worker-src 'self' blob:; " +
        "style-src 'self'; " +
        "style-src-elem 'self' 'unsafe-inline'; " +
        // Monaco renders positioned text and decorations through HTML style attributes. This
        // exception applies only to the editor document, never to the Aetheus application shell.
        "style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'self'; " +
        "base-uri 'none'; " +
        "form-action 'none'" +
        CspReporting.Directive(reportUri);
}
