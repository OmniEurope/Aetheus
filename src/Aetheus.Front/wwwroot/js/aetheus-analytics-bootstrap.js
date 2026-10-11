// SPDX-License-Identifier: EUPL-1.2

const configurationEndpoint = "/aetheus-analytics/v1/configuration";
const analyticsModule = "/_content/Aetheus.WebAnalytics/0.1.0/aetheus-web-analytics.js";

// Only known Aetheus top-level routes are measured. Every non-static nested segment is
// replaced before it can leave the browser, covering numeric ids, SHAs, slugs, branch names,
// settings tabs, and future route parameters without maintaining a list of user values.
// Recette R-471: the sign-in page is not measured. It is the one page a visitor sees before the
// application knows who they are; counted, it would add an anonymous visitor beside the account the
// same person signs in with a moment later.
// Recette R2-008: both lists follow the @page routes of Aetheus.Front (a front test fails when a
// static segment of a route is missing); `monitoring` and `notifications` were missing, so those
// pages were never counted.
const topLevelSegments = new Set([
  "account", "admin", "ai-tasks", "alerts", "analysis", "api-reference", "artifacts",
  "audit", "backups", "dashboards", "environments", "git", "git-repositories", "help",
  "logs", "monitoring", "not-found", "notifications", "pipelines", "plugins", "projects",
  "releases", "servers", "service-connections", "settings", "tasks", "templates", "users",
  "variable-libraries", "vaults"
]);

const staticNestedSegments = new Set([
  "accounts", "add-agent", "ai-profiles", "ai-tasks", "apache", "api-reference", "apps", "artifacts", "audit",
  "backups", "board", "branches", "certbot", "change-password", "commits", "configuration",
  "cron", "dashboards", "docker", "edit", "environments", "external-repo", "extract",
  "findings", "firewall", "fleet", "libraries", "logs", "mail", "mailbox", "modules", "monitoring",
  "new", "notifications", "organizations", "overview", "package-feeds", "package-registry",
  "performance", "pipelines", "plugins", "ports", "portsentry", "projects", "promote",
  "properties", "quality", "releases", "rkhunter", "roles", "runs", "servers", "services",
  "settings", "setup", "system-logs", "tasks", "teamspeak", "template", "update", "updates",
  "users", "vaults", "versions"
]);

// Recette R2-008: a `?tab=` (or any query) never enters the route label. The label is the page's
// route, a closed set the server accepts without a query string; the query can carry user values
// (`?projectId=`, `?app=`). A tab switch still counts as a page view of its page: the package only
// skips the same address twice in a row.
export function resolveAetheusAnalyticsRoute(pathname) {
  if (pathname === "/") return "/";
  if (typeof pathname !== "string" || pathname.length > 256 || !pathname.startsWith("/")) {
    return undefined;
  }

  const segments = pathname.split("/").filter(Boolean);
  const topLevel = segments[0]?.toLowerCase();
  if (!topLevel || !topLevelSegments.has(topLevel)) return undefined;

  return `/${segments.map((segment, index) => {
    const normalized = segment.toLowerCase();
    if (index === 0 || staticNestedSegments.has(normalized)) return normalized;
    return "{value}";
  }).join("/")}`;
}

// Recette R-471: how long the application may take to say whether someone is signed in. Recette
// R2-008: the page views of that wait are no longer lost; they are held by the package and sent with
// the identity once it is known (or anonymously when the wait ends without an answer).
const sessionWaitMilliseconds = 20000;

export function sessionKnown(waitMilliseconds = sessionWaitMilliseconds) {
  if (globalThis.Aetheus?._analyticsSessionKnown === true) return Promise.resolve();
  return new Promise((resolve) => {
    const timeout = setTimeout(resolve, waitMilliseconds);
    globalThis.Aetheus ??= {};
    globalThis.Aetheus._onAnalyticsIdentity = () => {
      clearTimeout(timeout);
      resolve();
    };
  });
}

function visitor() {
  try {
    return globalThis.Aetheus?.analyticsVisitor?.();
  } catch {
    return undefined;
  }
}

/**
 * Recette R2-008: installs the measurement at once, so every navigation from the first page on is
 * seen, and identifies the visitor once the application knows who is signed in. Until then the
 * package holds the events (holdUntilIdentified) instead of sending them anonymously.
 */
export async function startAetheusAnalytics(createAetheusAnalytics, waitForSession = sessionKnown) {
  const analytics = createAetheusAnalytics({
    routeResolver: resolveAetheusAnalyticsRoute,
    captureErrors: true,
    capturePerformance: true,
    holdUntilIdentified: true,
    // A yes/no answered by the running app from memory (layout.js); no storage is read here.
    isSignedIn: () => globalThis.Aetheus?.analyticsSignedIn?.() === true
  });
  analytics.install();
  await waitForSession();
  // The opaque identifier of the signed-in account (R-471), asked of the running app like the yes/no
  // above and again whenever the app says the visitor changed.
  analytics.identify(visitor());
  globalThis.Aetheus ??= {};
  globalThis.Aetheus._onAnalyticsIdentity = () => analytics.identify(visitor());
  return analytics;
}

async function installAnalytics() {
  try {
    const response = await fetch(configurationEndpoint, {
      credentials: "same-origin",
      cache: "no-store",
      headers: { accept: "application/json" }
    });
    if (!response.ok || !response.headers.get("content-type")?.includes("application/json")) return;

    const configuration = await response.json();
    if (configuration.enabled !== true) return;

    const { createAetheusAnalytics } = await import(analyticsModule);
    await startAetheusAnalytics(createAetheusAnalytics);
  } catch {
    // Monitoring must never affect application startup or navigation.
  }
}

void installAnalytics();
