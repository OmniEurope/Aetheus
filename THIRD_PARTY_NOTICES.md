# Third-party notices

This repository uses third-party packages under their respective licenses. Package versions are centrally declared in `Directory.Packages.props`. CI enforces vulnerability scans and project SPDX headers; the redistributed-dependency license inventory is reviewed manually and is not currently protected by a fail-closed CI guard.

## OmniEurope.Blazor

- Component: `OmniEurope.Blazor`
- Purpose: component library for the maintained Blazor WebAssembly interface
- License: EUPL-1.2
- Source: nuget.org package `OmniEurope.Blazor` (version pinned in `Directory.Packages.props`)

The OmniEurope.Blazor source and redistributed assets remain under EUPL-1.2.

## OmniEurope.Diagnostics

- Component: `OmniEurope.Diagnostics`
- Purpose: in-process request duration measurement: the administration Performance page of Aetheus, and the per-route figures the `Aetheus.Telemetry` package exports
- License: EUPL-1.2
- Source: nuget.org package `OmniEurope.Diagnostics` (version pinned in `Directory.Packages.props`)

The OmniEurope.Diagnostics source and redistributed assets remain under EUPL-1.2.

## NetArchTest.Rules

- Package: `NetArchTest.Rules` 1.3.2
- Purpose: architecture assertions in the test projects
- License: MIT
- Upstream: `BenMorris/NetArchTest`
- Compliance note: the NuGet package metadata does not expose a license expression, so this local inventory records the upstream repository license explicitly.

The upstream copyright and MIT license text apply to NetArchTest.Rules. No project code is relicensed by this notice.

## DnsClient

- Package: `DnsClient` 1.8.0
- Purpose: TXT, MX, A/AAAA and PTR lookups for the mail DNS verification and diagnostics of the backend
- License: Apache-2.0 (NuGet license expression)
- Upstream: `MichaCo/DnsClient.NET`

The upstream copyright and Apache-2.0 license apply to DnsClient. No project code is relicensed by this notice.

## Monaco Editor

- Component: Monaco Editor 0.52.2 (`min/vs` build)
- Purpose: YAML and diff editor of the pipeline pages, served inside a sandboxed frame
- License: MIT, `Copyright (c) Microsoft Corporation`
- Source: vendored in `src/Aetheus.Front/wwwroot/lib/monaco-editor/`
- License text: [`licenses/monaco-editor-MIT.txt`](licenses/monaco-editor-MIT.txt)

One vendored file (`basic-languages/swift/swift.js`) also carries `Copyright (C) David Owens II, owensd.io`.

### DOMPurify (bundled in Monaco)

- Component: DOMPurify 3.1.7, inside `editor.main.js`
- License: dual Apache-2.0 and MPL-2.0 per the vendored notice, redistributed here under Apache-2.0
- License text: [`licenses/dompurify-3.1.7.txt`](licenses/dompurify-3.1.7.txt) and [`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt)

### Codicons (shipped with Monaco)

- Component: `codicon.ttf` (vscode-codicons icon font)
- License: CC-BY-4.0, copyright Microsoft Corporation, attribution per upstream
- Notice: [`licenses/codicon-CC-BY-4.0.txt`](licenses/codicon-CC-BY-4.0.txt) (the full CC-BY-4.0 legal code is linked, not copied)

## dagre

- Component: dagre 1.1.4 with `@dagrejs/graphlib` 2.2.4 bundled in one minified file
- Purpose: layered graph layout of the visual pipeline view
- License: MIT (dagre, `Copyright (c) 2012-2014 Chris Pettitt`); graphlib carries a BSD-3-Clause style notice (`Copyright (c) 2014, Chris Pettitt`)
- Source: vendored in `src/Aetheus.Front/wwwroot/lib/dagre/dagre.min.js`
- License text: [`licenses/dagre-MIT.txt`](licenses/dagre-MIT.txt)

## External analysis tools

Aetheus can orchestrate external security and quality tools. They are not copied into this repository or bundled in Aetheus images. Their versions, license expressions, immutable provenance and integration modes are listed in [`docs/security/scanner-license-matrix.md`](docs/security/scanner-license-matrix.md) and [`scanner-manifest.json`](scanner-manifest.json).

The current external tool set includes OpenGrep (LGPL-2.1-only), Gitleaks (MIT), Trivy (Apache-2.0), Syft (Apache-2.0), OWASP ZAP (Apache-2.0), Dependency-Track (Apache-2.0), ESLint (MIT), Istanbul/nyc (ISC), Ruff (MIT), coverage.py (Apache-2.0), PMD (BSD-3-Clause) and jscpd (MIT). Their upstream notices apply to their respective artifacts.

## Aetheus analysis rules

The OpenGrep rules authored in this repository are part of Aetheus and remain licensed under EUPL-1.2. They are not relicensed under the scanner's license.

## OpenTelemetry protocol definitions

- Component: `open-telemetry/opentelemetry-proto` v1.10.0
- Purpose: vendored OTLP metrics, logs, traces, resource, common, and collector `.proto` definitions
- License: Apache-2.0
- Upstream: `open-telemetry/opentelemetry-proto`
- Source: <https://github.com/open-telemetry/opentelemetry-proto/tree/v1.10.0>
- License: <https://github.com/open-telemetry/opentelemetry-proto/blob/v1.10.0/LICENSE>

The included protocol files retain their upstream copyright and Apache-2.0 license headers. No upstream OpenTelemetry code is relicensed by this notice.

## Vulnerability data sources

The vulnerability watch (PLAN-004, ADR-061) reads public advisory data and shows results from it. The data is not relicensed: each record keeps the licence of the database that published it, and the sources below are named in the daily recap e-mail (section "Known vulnerabilities") and wherever the watch shows a result. The list is kept in `VulnerabilityAttributions` (Back, `Components/VulnerabilityWatch`), next to the source catalog that decides what may be stored; a source added to the catalog without an entry makes the attribution list fail (and a test names it), so none is shown unnamed.

- OSV.dev (<https://osv.dev/>): aggregator of the dumps below, queried through its public bucket. Each record carries its own source and licence.
- GitHub Advisory Database, PyPI Advisory Database, Go Vulnerability Database, OSS-Fuzz, Erlang Ecosystem Foundation: CC-BY-4.0, attribution required.
- RustSec Advisory Database, Global Security Database: CC0-1.0.
- OpenSSF Malicious Packages: Apache-2.0. Drupal Advisory Database: MIT.
- Alpine Linux security database: CC-BY-SA-4.0, share-alike on the data (open question 2 of the plan, pending the legal review required before any sale). Ubuntu (CC-BY-SA-4.0) is disabled by default and named only when configured.
- Debian security tracker: licence not stated by OSV, active by the user's decision of 2026-10-30, pending the same legal review.
- CISA Known Exploited Vulnerabilities catalog: CC0-1.0, used without the CISA logo.
- Excluded: Chainguard and Wolfi (CC-BY-NC-ND-4.0, commercial use forbidden).
- endoflife.date (<https://endoflife.date/>): release, support and end-of-life dates of .NET, Node.js, PHP, Python, Debian and Alpine Linux. The licence read is the MIT licence of its repository; the licence of the published data was not read separately, pending the legal review.
- Runtime release facts: Microsoft's .NET release metadata, the Node.js release list (`nodejs.org/dist/index.json`) and php.net's release lists and change logs. Only facts of version (a fixed version number, a CVE identifier) are taken, no text. Their licences were not read or not found, pending the same legal review; the matrix is in `docs/security/scanner-license-matrix.md`, section "Sources de données de la veille CVE".