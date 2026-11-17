import assert from "node:assert/strict";
import test from "node:test";
import {
  createAetheusAnalytics,
  normalizeRoute,
  resolveRouteTemplate
} from "../src/aetheus-web-analytics.js";

test("normalizes numeric and UUID route values", () => {
  assert.equal(normalizeRoute("/Orders/42"), "/orders/{id}");
  assert.equal(
    normalizeRoute("/users/6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1"),
    "/users/{id}"
  );
});

test("rejects query strings and fragments", () => {
  assert.throws(() => normalizeRoute("/orders?id=42"));
  assert.throws(() => normalizeRoute("/orders#details"));
  assert.throws(() => normalizeRoute(`/${"a".repeat(256)}`));
});

test("resolves business identifiers only through an explicit route allow-list", () => {
  assert.equal(
    resolveRouteTemplate(
      "/Customers/acme-corporation/Orders/quarterly-renewal",
      ["/customers/{customerKey}/orders/{orderKey}"]
    ),
    "/customers/{customerkey}/orders/{orderkey}"
  );
  assert.equal(
    resolveRouteTemplate("/admin/secret-account", ["/customers/{customerKey}"]),
    undefined
  );
  assert.equal(resolveRouteTemplate("/customers/acme", []), undefined);
});

test("supports Angular-style SPA history hooks and restores them on stop", () => {
  const originalPushState = () => undefined;
  const originalReplaceState = () => undefined;
  const listeners = new Map();
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: true,
      doNotTrack: "0",
      sendBeacon: () => true
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/" },
      doNotTrack: "0",
      addEventListener: (name, callback) => listeners.set(name, callback),
      removeEventListener: (name) => listeners.delete(name)
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: {
      pushState: originalPushState,
      replaceState: originalReplaceState
    }
  });

  const analytics = createAetheusAnalytics({ routeResolver: (pathname) => pathname });
  analytics.install();
  const wrappedPushState = history.pushState;
  analytics.install();
  assert.equal(history.pushState, wrappedPushState);
  assert.equal(listeners.has("popstate"), true);

  analytics.stop();
  assert.equal(history.pushState, originalPushState);
  assert.equal(history.replaceState, originalReplaceState);
  assert.equal(listeners.has("popstate"), false);
});

test("tracks an initial static page without framework or persistent browser storage", async () => {
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/About" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: {
      pushState: () => undefined,
      replaceState: () => undefined
    }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  const analytics = createAetheusAnalytics({ routeResolver: (pathname) => pathname });
  analytics.install();

  const events = await Promise.all(payloads.map((payload) => payload.text().then(JSON.parse)));
  assert.equal(events.length, 1);
  assert.equal(events[0].kind, "page_view");
  assert.equal(events[0].route, "/about");
  assert.equal("cookie" in events[0], false);
  assert.equal("localStorage" in events[0], false);

  analytics.stop();
});

test("uses an application route resolver so dynamic slugs never leave the browser", async () => {
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/users/private-account-name" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: {
      pushState: () => undefined,
      replaceState: () => undefined
    }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  const analytics = createAetheusAnalytics({
    routeResolver: () => "/users/{id}"
  });
  analytics.install();

  const event = JSON.parse(await payloads[0].text());
  assert.equal(event.route, "/users/{id}");
  assert.equal(JSON.stringify(event).includes("private-account-name"), false);
  analytics.stop();
});

test("captures only explicitly enabled bounded browser signals", async () => {
  const listeners = new Map();
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/projects/42" },
      doNotTrack: "0",
      addEventListener: (name, callback) => listeners.set(name, callback),
      removeEventListener: (name) => listeners.delete(name)
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: {
      pushState: () => undefined,
      replaceState: () => undefined
    }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => pathname,
    captureErrors: true,
    capturePerformance: true
  });
  analytics.install();
  listeners.get("error")?.({ message: "must never be sent", filename: "secret.js" });
  listeners.get("unhandledrejection")?.({ reason: "must never be sent" });
  analytics.trackPerformance(123.6);
  analytics.trackPerformance(300001);

  const events = await Promise.all(payloads.map((payload) => payload.text().then(JSON.parse)));
  assert.deepEqual(
    events.map((event) => event.kind),
    ["page_view", "browser_error", "browser_error", "browser_performance"]
  );
  assert.equal(events[0].route, "/projects/{id}");
  assert.equal(events[1].errorType, "script_error");
  assert.equal(events[2].errorType, "unhandled_rejection");
  assert.equal(events[3].durationMs, 124);
  assert.equal(JSON.stringify(events).includes("must never be sent"), false);

  analytics.stop();
  assert.equal(listeners.has("error"), false);
  assert.equal(listeners.has("unhandledrejection"), false);
  assert.equal(listeners.has("load"), false);
});

test("identify() attaches the authenticated user id to subsequent events only", async () => {
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/account" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: { pushState: () => undefined, replaceState: () => undefined }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  const analytics = createAetheusAnalytics({ routeResolver: (pathname) => pathname });
  analytics.install();
  analytics.identify("internal-user-42");
  globalThis.window.location.pathname = "/account/settings";
  analytics.trackPageView();

  const events = await Promise.all(payloads.map((payload) => payload.text().then(JSON.parse)));
  assert.equal(events.length, 2);
  assert.equal("authenticatedUserId" in events[0], false);
  assert.equal(events[1].authenticatedUserId, "internal-user-42");

  analytics.stop();
});

test("isSignedIn adds a bare signedIn flag, never an account, and fails closed", async () => {
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/servers" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: { pushState: () => undefined, replaceState: () => undefined }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  let answer = true;
  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => pathname,
    isSignedIn: () => {
      if (answer === "throw") throw new Error("app not started");
      return answer;
    }
  });
  analytics.install();
  answer = false;
  globalThis.window.location.pathname = "/projects";
  analytics.trackPageView();
  answer = "throw";
  globalThis.window.location.pathname = "/releases";
  analytics.trackPageView();

  const events = await Promise.all(payloads.map((payload) => payload.text().then(JSON.parse)));
  assert.equal(events.length, 3);
  assert.equal(events[0].signedIn, true);
  assert.equal("authenticatedUserId" in events[0], false);
  assert.equal("signedIn" in events[1], false);
  assert.equal("signedIn" in events[2], false);

  analytics.stop();
});

test("heartbeat refreshes LastSeenAtUtc on a schedule without changing the route dedup", (t) => {
  t.mock.timers.enable({ apis: ["setInterval"] });
  const payloads = [];
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/dashboard" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: { pushState: () => undefined, replaceState: () => undefined }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });

  const analytics = createAetheusAnalytics({ routeResolver: (pathname) => pathname });
  analytics.install();
  t.mock.timers.tick(75_000);
  t.mock.timers.tick(75_000);
  analytics.stop();
  t.mock.timers.tick(75_000);

  return Promise.all(payloads.map((payload) => payload.text().then(JSON.parse))).then((events) => {
    assert.deepEqual(
      events.map((event) => event.kind),
      ["page_view", "heartbeat", "heartbeat"]
    );
    assert.equal(events.every((event) => event.route === "/dashboard"), true);
  });
});

test("a tab that becomes visible again sends a heartbeat at once, never while hidden and never twice in a row", async (t) => {
  t.mock.timers.enable({ apis: ["setInterval", "Date"], now: 1_000_000 });
  const payloads = [];
  const documentListeners = new Map();
  const fakeDocument = {
    visibilityState: "visible",
    readyState: "complete",
    addEventListener: (name, callback) => documentListeners.set(name, callback),
    removeEventListener: (name) => documentListeners.delete(name)
  };
  const setVisibility = (state) => {
    fakeDocument.visibilityState = state;
    documentListeners.get("visibilitychange")?.();
  };
  Object.defineProperty(globalThis, "document", { configurable: true, value: fakeDocument });
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname: "/dashboard" },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", {
    configurable: true,
    value: { pushState: () => undefined, replaceState: () => undefined }
  });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });
  const kinds = async () => (await Promise.all(payloads.map((payload) => payload.text().then(JSON.parse))))
    .map((event) => event.kind);

  const analytics = createAetheusAnalytics({ routeResolver: (pathname) => pathname });
  analytics.install();
  assert.deepEqual(await kinds(), ["page_view"]);

  // Hidden: the scheduled beat is skipped, and hiding itself sends nothing.
  setVisibility("hidden");
  t.mock.timers.tick(75_000);
  assert.deepEqual(await kinds(), ["page_view"]);

  // Visible again: the beat leaves at once, not up to 75 s later.
  setVisibility("visible");
  assert.deepEqual(await kinds(), ["page_view", "heartbeat"]);

  // A flip hidden then visible right after a beat does not send a second one.
  setVisibility("hidden");
  setVisibility("visible");
  assert.deepEqual(await kinds(), ["page_view", "heartbeat"]);

  // The schedule restarted at the visible beat: the next scheduled one comes a full interval later.
  t.mock.timers.tick(75_000);
  assert.deepEqual(await kinds(), ["page_view", "heartbeat", "heartbeat"]);

  analytics.stop();
  assert.equal(documentListeners.has("visibilitychange"), false);
  Reflect.deleteProperty(globalThis, "document");
});

function browserWithBeacon(pathname, search = "") {
  const payloads = [];
  const history = { pushState: () => undefined, replaceState: () => undefined };
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {
      globalPrivacyControl: false,
      doNotTrack: "0",
      sendBeacon: (_endpoint, blob) => {
        payloads.push(blob);
        return true;
      }
    }
  });
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: { pathname, search },
      doNotTrack: "0",
      addEventListener: () => undefined,
      removeEventListener: () => undefined
    }
  });
  Object.defineProperty(globalThis, "history", { configurable: true, value: history });
  Object.defineProperty(globalThis, "crypto", {
    configurable: true,
    value: { randomUUID: () => "6b40f4fd-53ac-4f84-8c9e-9fac6feec6e1" }
  });
  return {
    history,
    navigate(nextPathname, nextSearch = "", replace = false) {
      globalThis.window.location.pathname = nextPathname;
      globalThis.window.location.search = nextSearch;
      if (replace) history.replaceState({}, "", nextPathname + nextSearch);
      else history.pushState({}, "", nextPathname + nextSearch);
    },
    events: () => Promise.all(payloads.map((payload) => payload.text().then(JSON.parse)))
  };
}

test("R2-008: another entity or another ?tab= of the same route counts; the same address does not", async () => {
  const browser = browserWithBeacon("/pipelines/12");
  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => pathname.replace(/\/\d+/g, "/{value}")
  });
  analytics.install();

  browser.navigate("/pipelines/13");
  browser.navigate("/pipelines/13", "?tab=runs");
  browser.navigate("/pipelines/13", "?tab=runs", true);
  browser.navigate("/pipelines/13", "?tab=yaml");

  const events = await browser.events();
  assert.deepEqual(events.map((event) => event.route), [
    "/pipelines/{value}", "/pipelines/{value}", "/pipelines/{value}", "/pipelines/{value}"
  ]);
  assert.equal(JSON.stringify(events).includes("tab="), false);
  analytics.stop();
});

test("R2-008: coming back to a measured page after an unmeasured one counts again", async () => {
  const browser = browserWithBeacon("/projects");
  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => (pathname === "/login" ? undefined : pathname)
  });
  analytics.install();

  browser.navigate("/login");
  browser.navigate("/projects");

  const events = await browser.events();
  assert.deepEqual(events.map((event) => event.route), ["/projects", "/projects"]);
  analytics.stop();
});

test("R2-008: holdUntilIdentified keeps the first page views and sends them with the identity", async () => {
  const browser = browserWithBeacon("/projects");
  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => pathname,
    holdUntilIdentified: true
  });
  analytics.install();
  browser.navigate("/servers");

  assert.equal((await browser.events()).length, 0);
  analytics.identify("internal-user-42");
  browser.navigate("/releases");

  const events = await browser.events();
  assert.deepEqual(events.map((event) => event.route), ["/projects", "/servers", "/releases"]);
  assert.equal(events.every((event) => event.authenticatedUserId === "internal-user-42"), true);
  analytics.stop();
});

test("R2-008: an anonymous identify() releases the held page views without an account", async () => {
  const browser = browserWithBeacon("/projects");
  const analytics = createAetheusAnalytics({
    routeResolver: (pathname) => pathname,
    holdUntilIdentified: true
  });
  analytics.install();
  analytics.identify(undefined);

  const events = await browser.events();
  assert.equal(events.length, 1);
  assert.equal("authenticatedUserId" in events[0], false);
  analytics.stop();
});
