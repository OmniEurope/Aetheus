# Aetheus.WebAnalytics

`Aetheus.WebAnalytics` provides optional audience measurement without analytical cookies, `localStorage`,
cross-site tracking, or browser fingerprinting. Anonymous unique counts are estimates. Authenticated users
are represented by an application-scoped HMAC and are never sent with their source account identifier.

The `.nupkg` is distributed exclusively through the internal Aetheus NuGet registry. Configure the
private source and its runtime credential as documented in
[`docs/runbooks/package-registry.md`](../../docs/runbooks/package-registry.md); it is never published to
nuget.org (ADR-037). The embedded browser package follows the same internal-only decision for the Aetheus
npm registry.

```csharp
builder.Services.AddAetheusWebAnalytics(builder.Configuration);
// after UseRouting:
app.MapAetheusWebAnalytics();
```

The feature is off by default. `AETHEUS_WEB_ANALYTICS_ENABLED` has final say. When enabled, configure an
HTTPS ingestion endpoint, ingestion key, 32-character pseudonymization key, application/site identifiers,
and the controller/contact/purpose/legal-basis/hosting text displayed on
`/privacy/audience-measurement`.

A host that does not authenticate its visitors (a static server in front of a single-page application)
sets `AcceptDeclaredUserId=true`: the browser event may then carry the contract's `authenticatedUserId`, an
opaque identifier (`^[A-Za-z0-9_-]+$`, never an account name or an e-mail address) that only feeds the
pseudonym derivation. Off by default, and the host's own authentication wins when it has one. Behind a
reverse proxy the host must also call `UseForwardedHeaders`, or every anonymous visitor shares the
proxy's address.

The Razor information page is embedded but remains off until `EnablePrivacyPage=true` is explicitly set.
Enabling it without the controller, contact, purpose, legal basis, and hosting fields fails startup rather
than displaying incomplete legal information.

`AudienceMeasurementLink` is the optional footer link. A host may disable the embedded page and serve its
own `/privacy/audience-measurement` page. For signed-in accounts, provide
`AuthenticatedOptOutResolver` and `AuthenticatedOptOutWriter` callbacks to persist refusal in the account;
anonymous refusal falls back to the single essential `aetheus_analytics_optout` cookie.

Include the immutable, package-versioned asset
`/_content/Aetheus.WebAnalytics/<package version>/aetheus-web-analytics.js` in the host (for example
`/_content/Aetheus.WebAnalytics/1.1.0/aetheus-web-analytics.js` with package 1.1.0). The script tracks initial and
SPA route page views without persistent browser state. Browser errors and navigation durations remain
disabled unless `captureErrors` and `capturePerformance` are explicitly `true`; error messages, stacks,
resource URLs and user input are never collected. Global Privacy Control and Do Not Track are honored.
Anonymous refusal uses only the essential `aetheus_analytics_optout` preference cookie.

For routes containing application slugs or other dynamic string identifiers, configure the JavaScript
`routeResolver` to return the framework route template (for example `/users/{id}`), or provide the
optional `routeTemplates` allow-list. The browser library emits nothing only when neither mechanism
resolves the current pathname, because a raw path can contain a slug, account name, or reset token that
cannot be distinguished from static navigation text.

Generate the SRI value from the exact deployed file and pin it in the host markup:

```bash
openssl dgst -sha384 -binary aetheus-web-analytics.js | openssl base64 -A
```

Use a CSP such as `script-src 'self'` when the asset is served by the host. For a separate asset origin,
add only that exact HTTPS origin and configure the Aetheus public ingest origin allow-list accordingly.
