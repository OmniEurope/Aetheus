# @aetheus/web-analytics

Storage-free page-view and SPA-route measurement for Aetheus. It creates no analytical cookie or
`localStorage` entry, sends no query string, fragment, DOM, form value, token, or persistent browser ID,
and honors Global Privacy Control and Do Not Track.

```js
import { createAetheusAnalytics } from "@aetheus/web-analytics";
createAetheusAnalytics({
  captureErrors: false,
  capturePerformance: false,
  routeResolver: (pathname) => pathname
}).install();
```

`captureErrors` and `capturePerformance` are independently opt-in. Error capture sends only the fixed
categories `script_error` or `unhandled_rejection`, never a message or stack. Performance capture sends
only a bounded navigation duration and normalized route. Applications may call
`trackPerformance(durationMs)` for an explicitly measured SPA transition.

If a route can contain a username, slug, document name, or other application identifier, `routeResolver`
must return the framework's route template instead of the concrete path, for example
`"/users/{id}"`. The callback receives only `pathname`, never the query string or fragment. Omitting
`routeResolver` falls back to the optional `routeTemplates` allow-list; emission is disabled only when
neither mechanism resolves the current pathname. The identity resolver shown above is appropriate only
when every non-numeric path segment is guaranteed to be public static navigation text.

A background heartbeat (every 75s, paused while the tab is hidden) refreshes the visit's server-side
`LastSeenAtUtc` on the current route without inflating page-view counts. Call `identify(userId)` with an
opaque id already known to the host application (never an email or other raw PII) to mark subsequent
events as authenticated; the backend hashes it server-side into `AuthenticatedPseudonym` and never stores
it raw. `window.__AETHEUS_ANALYTICS_USER__`, if set before `install()`, seeds the same identity.

**`identify()` works with both endpoints.** The Aetheus public ingest endpoint
(`api/ingest/web-analytics/v1/public/{siteId}`) always reads it. The self-hosted endpoint shipped by the
`Aetheus.WebAnalytics` Razor Class Library derives identity server-side first, through
`AetheusWebAnalyticsOptions.AuthenticatedUserIdResolver`, and that session identity always wins. It reads
the `authenticatedUserId` declared by the browser only when the host opts in with `AcceptDeclaredUserId`
(`Aetheus:WebAnalytics:AcceptDeclaredUserId`, off by default) and the session provides no identity; the
value is then restricted to `^[A-Za-z0-9_-]+$`. That mode serves a single-page application host that never
sees who is signed in. See the [web-analytics-v1 contract](../../docs/contracts/web-analytics-v1.md). The
RCL endpoint also accepts heartbeats, and a bare `signedIn: true` flag set by the
`isSignedIn: () => boolean` option: a host whose users authenticate against another origin (a bearer-token
API) can then count signed-in visits without
sending any account or token. Such a visit is counted per network prefix and per month, never under a
stable identity.

The same source is packaged by the Razor Class Library at
`/_content/Aetheus.WebAnalytics/0.1.0/aetheus-web-analytics.js`. Pin the exact version and its SHA-384 SRI
value; do not use an unversioned mutable URL.
