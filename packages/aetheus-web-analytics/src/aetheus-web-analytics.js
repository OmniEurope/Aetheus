const DEFAULT_ENDPOINT = "/aetheus-analytics/v1/events";
const HEARTBEAT_INTERVAL_MS = 75_000;
// A tab that becomes visible again beats at once (its next scheduled beat could be 75 s away), unless a page view
// or a heartbeat already left within this gap.
const VISIBLE_BEAT_MIN_GAP_MS = 10_000;
// Most events kept while waiting for the identity (holdUntilIdentified); later ones are dropped.
const MAX_HELD_EVENTS = 50;

export function normalizeRoute(pathname) {
  if (typeof pathname !== "string"
      || pathname.length > 256
      || !pathname.startsWith("/")
      || pathname.includes("?")
      || pathname.includes("#")) {
    throw new TypeError("A rooted path without query string or fragment is required.");
  }

  const segments = pathname.split("/").filter(Boolean).map((segment) => {
    if (/^\d+$/.test(segment) || /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(segment)) {
      return "{id}";
    }
    return segment.toLowerCase();
  });
  return `/${segments.join("/")}`;
}

export function resolveRouteTemplate(pathname, templates) {
  const normalizedPath = normalizeRoute(pathname);
  if (!Array.isArray(templates) || templates.length === 0) {
    return undefined;
  }

  const pathSegments = normalizedPath.split("/").filter(Boolean);
  for (const template of templates) {
    if (typeof template !== "string") {
      continue;
    }
    let normalizedTemplate;
    try {
      normalizedTemplate = normalizeRoute(template);
    } catch {
      continue;
    }
    const templateSegments = normalizedTemplate.split("/").filter(Boolean);
    if (templateSegments.length !== pathSegments.length) {
      continue;
    }
    const matches = templateSegments.every((segment, index) =>
      /^\{[a-z][a-z0-9_-]*\}$/i.test(segment)
      || segment === pathSegments[index]);
    if (matches) {
      return normalizedTemplate;
    }
  }
  return undefined;
}

export function createAetheusAnalytics(configuration = {}) {
  const endpoint = configuration.endpoint ?? DEFAULT_ENDPOINT;
  const routeResolver = configuration.routeResolver;
  const routeTemplates = configuration.routeTemplates;
  const respectDoNotTrack = configuration.respectDoNotTrack !== false;
  const captureErrors = configuration.captureErrors === true;
  const capturePerformance = configuration.capturePerformance === true;
  // Whether the visitor is signed in, as a plain yes/no asked of the host app at send time. Nothing
  // identifies the account: the collector counts signed-in visits under the same network-prefix
  // pseudonym as anonymous ones. Only used when no authenticatedUserId is known.
  const isSignedIn = typeof configuration.isSignedIn === "function" ? configuration.isSignedIn : undefined;
  // Recette R2-008: with holdUntilIdentified, the events of the first moments (the first page view and
  // the navigations before the host app knows who is signed in) are kept in memory, then sent with the
  // identity at the first identify() call. Installing at once no longer loses those navigations, and
  // waiting for the identity no longer counts the same person twice (anonymous, then signed in).
  let holding = configuration.holdUntilIdentified === true;
  const held = [];
  let stopped = false;
  let installed = false;
  // Recette R2-008: the last page address counted (path and query, never the fragment). Only the same
  // address twice in a row is one view: another entity of the same route, or another ?tab= of the same
  // page, is a navigation and counts, under the page's route label.
  let previousLocation;
  let originalPushState;
  let originalReplaceState;
  let pushStateWrapper;
  let replaceStateWrapper;
  let heartbeatIntervalId;
  let lastBeatAt = 0;
  let authenticatedUserId = typeof window !== "undefined"
    && typeof window.__AETHEUS_ANALYTICS_USER__ === "string"
    && window.__AETHEUS_ANALYTICS_USER__.length > 0
    ? window.__AETHEUS_ANALYTICS_USER__
    : undefined;

  function identify(userId) {
    authenticatedUserId = typeof userId === "string" && userId.length > 0 ? userId : undefined;
    release();
  }

  /** Sends the held events, with the identity known now; later events go out at once. */
  function release() {
    holding = false;
    for (const pending of held.splice(0)) {
      send(pending);
    }
  }

  function privacySignalEnabled() {
    return navigator.globalPrivacyControl === true
      || (respectDoNotTrack && (navigator.doNotTrack === "1" || window.doNotTrack === "1"));
  }

  function signedIn() {
    try {
      return isSignedIn?.() === true;
    } catch {
      return false;
    }
  }

  function currentRoute() {
    try {
      if (typeof routeResolver === "function") {
        return normalizeRoute(routeResolver(window.location.pathname));
      }
      return resolveRouteTemplate(window.location.pathname, routeTemplates);
    } catch {
      return undefined;
    }
  }

  function emit(kind, details = {}) {
    if (stopped || privacySignalEnabled()) {
      return;
    }

    const route = currentRoute();
    if (!route) {
      return;
    }

    if (kind === "page_view" || kind === "heartbeat") {
      lastBeatAt = Date.now();
    }

    // The route and the time are those of the moment; the identity is added when the event leaves.
    const pending = {
      envelope: {
        schemaVersion: 1,
        eventId: crypto.randomUUID(),
        occurredAtUtc: new Date().toISOString(),
        kind,
        route
      },
      details
    };
    if (holding) {
      if (held.length < MAX_HELD_EVENTS) {
        held.push(pending);
      }
      return;
    }
    send(pending);
  }

  function send({ envelope, details }) {
    if (stopped || privacySignalEnabled()) {
      return;
    }
    const event = JSON.stringify({
      ...envelope,
      ...(authenticatedUserId ? { authenticatedUserId } : signedIn() ? { signedIn: true } : {}),
      ...details
    });
    if (event.length > 1024) {
      return;
    }

    if (typeof navigator.sendBeacon === "function"
        && navigator.sendBeacon(endpoint, new Blob([event], { type: "application/json" }))) {
      return;
    }

    void fetch(endpoint, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: event,
      credentials: "same-origin",
      keepalive: true
    }).catch(() => undefined);
  }

  function emitPageView() {
    const location = currentLocation();
    if (location === previousLocation) {
      return;
    }
    // Kept even when the address is not measured, so coming back to a measured page counts again.
    previousLocation = location;
    emit("page_view");
  }

  function currentLocation() {
    const { pathname, search } = window.location;
    return `${pathname}${typeof search === "string" ? search : ""}`;
  }

  function emitScriptError() {
    if (captureErrors) {
      emit("browser_error", { errorType: "script_error" });
    }
  }

  function emitUnhandledRejection() {
    if (captureErrors) {
      emit("browser_error", { errorType: "unhandled_rejection" });
    }
  }

  function emitPerformance(durationMs) {
    if (!capturePerformance
        || !Number.isFinite(durationMs)
        || durationMs < 0
        || durationMs > 300000) {
      return;
    }
    emit("browser_performance", { durationMs: Math.round(durationMs) });
  }

  function emitHeartbeat() {
    if (typeof document !== "undefined" && document.visibilityState === "hidden") {
      return;
    }
    // Bypasses the address dedup in emitPageView: a heartbeat must fire on schedule
    // even while the visitor stays on the same route, to keep LastSeenAtUtc fresh server-side.
    emit("heartbeat");
  }

  function onVisibilityChange() {
    if (document.visibilityState !== "visible" || Date.now() - lastBeatAt < VISIBLE_BEAT_MIN_GAP_MS) {
      return;
    }
    emitHeartbeat();
    // Restart the schedule so the next scheduled beat is a full interval after this one, not seconds after.
    if (heartbeatIntervalId !== undefined) {
      clearInterval(heartbeatIntervalId);
      heartbeatIntervalId = setInterval(emitHeartbeat, HEARTBEAT_INTERVAL_MS);
    }
  }

  function emitNavigationPerformance() {
    if (!capturePerformance || typeof performance === "undefined") {
      return;
    }
    const navigation = performance.getEntriesByType?.("navigation")?.[0];
    if (navigation) {
      emitPerformance(navigation.duration);
    }
  }

  function install() {
    if (installed || stopped) {
      return;
    }
    installed = true;
    originalPushState = history.pushState;
    originalReplaceState = history.replaceState;
    pushStateWrapper = function (...args) {
      originalPushState.apply(this, args);
      emitPageView();
    };
    replaceStateWrapper = function (...args) {
      originalReplaceState.apply(this, args);
      emitPageView();
    };
    history.pushState = pushStateWrapper;
    history.replaceState = replaceStateWrapper;
    window.addEventListener("popstate", emitPageView);
    if (captureErrors) {
      window.addEventListener("error", emitScriptError);
      window.addEventListener("unhandledrejection", emitUnhandledRejection);
    }
    if (capturePerformance) {
      if (typeof document !== "undefined" && document.readyState === "complete") {
        emitNavigationPerformance();
      } else {
        window.addEventListener("load", emitNavigationPerformance);
      }
    }
    if (typeof document !== "undefined" && typeof document.addEventListener === "function") {
      document.addEventListener("visibilitychange", onVisibilityChange);
    }
    heartbeatIntervalId = setInterval(emitHeartbeat, HEARTBEAT_INTERVAL_MS);
    emitPageView();
  }

  function stop() {
    stopped = true;
    held.length = 0;
    installed = false;
    window.removeEventListener("popstate", emitPageView);
    window.removeEventListener("error", emitScriptError);
    window.removeEventListener("unhandledrejection", emitUnhandledRejection);
    window.removeEventListener("load", emitNavigationPerformance);
    if (typeof document !== "undefined" && typeof document.removeEventListener === "function") {
      document.removeEventListener("visibilitychange", onVisibilityChange);
    }
    if (heartbeatIntervalId !== undefined) {
      clearInterval(heartbeatIntervalId);
      heartbeatIntervalId = undefined;
    }
    if (history.pushState === pushStateWrapper) {
      history.pushState = originalPushState;
    }
    if (history.replaceState === replaceStateWrapper) {
      history.replaceState = originalReplaceState;
    }
  }

  return {
    install,
    stop,
    identify,
    trackPageView: emitPageView,
    trackPerformance: emitPerformance
  };
}
