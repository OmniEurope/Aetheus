// SPDX-License-Identifier: EUPL-1.2

const configurationEndpoint = "/aetheus-analytics/v1/configuration";
const analyticsModule = "/_content/Aetheus.WebAnalytics/1.1.1/aetheus-web-analytics.js";

// Measures only known routes, replacing dynamic segments before they leave the browser. Sign-in
// pages are excluded to avoid counting one person both anonymously and as an authenticated visitor.
const topLevelSegments = new Set([
  "account", "admin", "ai-tasks", "alerts", "analysis", "api-reference", "artifacts",
  "audit", "backups", "dashboards", "environments", "git", "git-repositories", "help",
  "logs", "mail", "monitoring", "not-found", "notifications", "pipelines", "plugins", "projects",
  "releases", "servers", "service-connections", "settings", "tasks", "templates", "users",
  "variable-libraries", "vaults"
]);

const staticNestedSegments = new Set([
  "accounts", "add-agent", "ai-profiles", "ai-tasks", "apache", "api-reference", "apps", "artifacts", "audit",
  "backups", "board", "branches", "certbot", "change-password", "commits", "configuration",
  "cron", "dashboards", "docker", "edit", "environments", "external-repo", "extract",
  "findings", "firewall", "fleet", "libraries", "logs", "mail", "mail-templates", "mailbox", "modules", "monitoring",
  "new", "notifications", "organizations", "overview", "package-feeds", "package-registry",
  "performance", "pipelines", "plugins", "ports", "portsentry", "projects", "promote",
  "properties", "quality", "releases", "rkhunter", "roles", "runs", "servers", "services",
  "settings", "setup", "system-logs", "tasks", "teamspeak", "template", "update", "updates",
  "users", "vaults", "versions"
]);

// Excludes query strings from route labels because they can contain user values. Tab navigation
// still counts as a page view when the URL changes.
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

// Bounds the wait for application identity; buffered page views use that identity when available or
// become anonymous when the wait expires.
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

/* Installs navigation measurement immediately and buffers events until visitor identification
   completes. */
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
  // Reads the opaque visitor identifier from the application initially and whenever the signed-in
  // visitor changes.
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
