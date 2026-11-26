"use strict";

// Runs one BadgeScriptTests scenario: loads the real sw.js or push.js into a node:vm context with stub globals (the
// room.js globals, a recording self.registration and a fake navigator with the Badging API), runs the named scenario
// and prints its result as one JSON line. Usage: node BadgeHarness.js <scenario.json>, where the file holds
// { "name": "...", "wwwroot": "<folder>" }.
// No timer, no clock and no network: fetch and caches throw if called, and every await is on a returned promise (or
// one turn of the event loop), never a delay.

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const scenario = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise: promise, resolve: resolve, reject: reject };
}

// One turn of the event loop, so every settled promise chain has run.
function drain() {
  return new Promise((resolve) => setImmediate(resolve));
}

function forbidden(name) {
  return () => {
    throw new Error(name + " must not be called");
  };
}

// The values a badge is checked against: label -> value; "missing" leaves the field out.
const ValidBadges = { one: 1, seven: 7, max: 999 };
const InvalidBadges = {
  missing: undefined,
  negative: -1,
  tooBig: 1000,
  fraction: 2.5,
  string: "3",
  stringZero: "0",
  boolean: true,
  null: null,
  object: { count: 3 },
  array: [3],
  notANumber: Number.NaN,
  infinity: Number.POSITIVE_INFINITY,
};

// A fake navigator whose Badging API records each call into log. mode: "ok" (resolves), "missing" (no Badging
// API), "throws" (throws synchronously), "rejects" (returns a rejected promise) or "deferred" (returns a promise
// the scenario settles through pending).
function fakeNavigator(mode, log, pending) {
  if (mode === "missing") {
    return { userAgent: "harness" };
  }
  const answer = (call) => {
    log.push(call);
    if (mode === "throws") {
      throw new Error("badge throws");
    }
    if (mode === "rejects") {
      return Promise.reject(new Error("badge rejects"));
    }
    if (mode === "deferred") {
      const next = deferred();
      pending.push(next);
      return next.promise;
    }
    return Promise.resolve();
  };
  return {
    userAgent: "harness",
    setAppBadge(count) {
      return answer("set:" + (arguments.length === 1 ? JSON.stringify(count) : "args=" + arguments.length));
    },
    clearAppBadge() {
      return answer("clear" + (arguments.length === 0 ? "" : ":args=" + arguments.length));
    },
  };
}

// The room.js globals the RoomKit scripts read.
function roomGlobals(target) {
  target.CacheVersion = 1;
  target.CacheName = "harness-1";
  target.Shell = ["/", "/room.js"];
  target.PushAppTitles = { updated: "Updated" };
  target.PushFallbackTitle = "Harness";
  target.PushDefaultTag = "harness";
  target.PushUpdatePrefix = "Version ";
  target.PushAppTexts = { homeScreen: "Add to Home Screen", denied: "Denied" };
}

// Loads sw.js into its own context. options.navigator is the badge mode, options.notify "ok" or "deferred".
function loadWorker(options) {
  const badgeLog = [];
  const badgePending = [];
  const notifications = [];
  const notifyPending = [];
  const handlers = {};
  const sandbox = {
    console: console,
    URL: URL,
    Promise: Promise,
    fetch: forbidden("fetch"),
    caches: { open: forbidden("caches.open"), keys: forbidden("caches.keys"), delete: forbidden("caches.delete") },
    location: { origin: "https://room.test" },
    clients: { matchAll: forbidden("clients.matchAll"), openWindow: forbidden("clients.openWindow") },
    registration: {
      showNotification(title, notificationOptions) {
        notifications.push({ title: title, options: JSON.parse(JSON.stringify(notificationOptions)) });
        if (options.notify === "deferred") {
          const next = deferred();
          notifyPending.push(next);
          return next.promise;
        }
        return Promise.resolve();
      },
    },
    addEventListener(name, handler) {
      handlers[name] = handler;
    },
    skipWaiting: forbidden("skipWaiting"),
  };
  sandbox.self = sandbox;
  sandbox.navigator = fakeNavigator(options.navigator, badgeLog, badgePending);
  sandbox.importScripts = (...urls) => {
    if (urls.length !== 1 || urls[0] !== "/room.js") {
      throw new Error("unexpected importScripts " + urls.join(","));
    }
    roomGlobals(sandbox);
  };
  const context = vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(scenario.wwwroot, "sw.js"), "utf8"), context, { filename: "sw.js" });
  return {
    badgeLog: badgeLog,
    badgePending: badgePending,
    notifications: notifications,
    notifyPending: notifyPending,
    // Dispatches one push with payload; returns the waitUntil promises and whether the handler threw.
    push(payload) {
      const waits = [];
      const event = {
        data: { json: () => payload },
        waitUntil(promise) {
          waits.push(promise);
        },
      };
      let threw = "";
      try {
        handlers.push(event);
      } catch (error) {
        threw = String(error);
      }
      return { waits: waits, threw: threw };
    },
  };
}

// Records how a promise settles, without waiting for anything else.
function track(promise) {
  const state = { value: "pending" };
  Promise.resolve(promise).then(() => {
    state.value = "resolved";
  }, () => {
    state.value = "rejected";
  });
  return state;
}

// One push on a fresh worker, awaited until its waitUntil promise settles.
async function pushOnce(payload, mode) {
  const worker = loadWorker({ navigator: mode || "ok", notify: "ok" });
  const sent = worker.push(payload);
  let state = "none";
  if (sent.waits.length === 1) {
    state = await sent.waits[0].then(() => "resolved", () => "rejected");
  }
  return {
    calls: worker.badgeLog,
    notifications: worker.notifications,
    waits: sent.waits.length,
    state: state,
    threw: sent.threw,
  };
}

function withBadge(base, value) {
  const payload = { ...base };
  if (value !== undefined) {
    payload.badge = value;
  }
  return payload;
}

const NeedsHuman = { kind: "needs-human", project: "Room", job: "12-some-job" };

const Kinds = {
  needsHuman: NeedsHuman,
  approval: { kind: "approval", project: "Room", count: 3 },
  update: { kind: "update", version: "1.2.3" },
  test: { kind: "test" },
  unknown: { kind: "no-such-kind", project: "Room", job: "12-some-job" },
};

// Loads push.js into its own context with a fake navigator in the given mode.
function loadPage(mode) {
  const badgeLog = [];
  const badgePending = [];
  const sandbox = {
    console: console,
    Promise: Promise,
    fetch: forbidden("fetch"),
    caches: { open: forbidden("caches.open") },
    authFetch: forbidden("authFetch"),
    navigator: fakeNavigator(mode, badgeLog, badgePending),
  };
  roomGlobals(sandbox);
  sandbox.window = sandbox;
  const context = vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(scenario.wwwroot, "push.js"), "utf8"), context, { filename: "push.js" });
  return { sandbox: sandbox, badgeLog: badgeLog, badgePending: badgePending };
}

// Calls setRoomKitBadge(value) and records its answer: the resolved value, "rejected:..." or "threw:...".
async function callHelper(page, value) {
  let promise;
  try {
    promise = page.sandbox.setRoomKitBadge(value);
  } catch (error) {
    return { answer: "threw:" + String(error), promise: false };
  }
  const isPromise = promise !== null && typeof promise === "object" && typeof promise.then === "function";
  let answer;
  try {
    answer = await promise;
  } catch (error) {
    answer = "rejected:" + String(error);
  }
  return { answer: answer, promise: isPromise };
}

const scenarios = {
  // AC-1, AC-2, AC-3: one push per badge value, each on a fresh worker.
  async workerValues() {
    const valid = {};
    for (const [label, value] of Object.entries(ValidBadges)) {
      valid[label] = await pushOnce(withBadge(NeedsHuman, value));
    }
    const invalid = {};
    for (const [label, value] of Object.entries(InvalidBadges)) {
      invalid[label] = await pushOnce(withBadge(NeedsHuman, value));
    }
    return {
      valid: valid,
      zero: await pushOnce(withBadge(NeedsHuman, 0)),
      negativeZero: await pushOnce(withBadge(NeedsHuman, -0)),
      invalid: invalid,
    };
  },

  // AC-1: the waitUntil promise settles only after both the badge call and showNotification have settled.
  async workerCovers() {
    const orders = {};
    for (const first of ["badge", "notification"]) {
      const worker = loadWorker({ navigator: "deferred", notify: "deferred" });
      const sent = worker.push(withBadge(NeedsHuman, 4));
      const state = track(sent.waits[0]);
      await drain();
      const steps = [state.value];
      const settle = (which) => (which === "badge" ? worker.badgePending : worker.notifyPending)[0].resolve();
      settle(first);
      await drain();
      steps.push(state.value);
      settle(first === "badge" ? "notification" : "badge");
      await sent.waits[0];
      await drain();
      steps.push(state.value);
      orders[first] = {
        steps: steps,
        waits: sent.waits.length,
        calls: worker.badgeLog,
        notifications: worker.notifications.length,
      };
    }
    return orders;
  },

  // AC-4: per kind, the notification with and without badge 7.
  async workerKinds() {
    const kinds = {};
    for (const [label, payload] of Object.entries(Kinds)) {
      kinds[label] = { plain: await pushOnce(payload), badged: await pushOnce(withBadge(payload, 7)) };
    }
    return kinds;
  },

  // AC-5: a missing, throwing or rejecting Badging API, for a set and a clear.
  async workerBroken() {
    const result = { plain: await pushOnce(NeedsHuman), modes: {} };
    for (const mode of ["missing", "throws", "rejects"]) {
      result.modes[mode] = {
        set: await pushOnce(withBadge(NeedsHuman, 5), mode),
        clear: await pushOnce(withBadge(NeedsHuman, 0), mode),
      };
    }
    return result;
  },

  // AC-6, AC-7: setRoomKitBadge over the value table and the Badging API modes; the log right after load.
  async page() {
    const result = { loads: {}, valid: {}, invalid: {}, modes: {} };
    for (const mode of ["ok", "missing", "throws", "rejects"]) {
      const page = loadPage(mode);
      await drain();
      result.loads[mode] = { calls: [...page.badgeLog], helper: typeof page.sandbox.setRoomKitBadge };
    }
    for (const [label, value] of Object.entries({ ...ValidBadges, zero: 0 })) {
      const page = loadPage("ok");
      const answer = await callHelper(page, value);
      result.valid[label] = { ...answer, calls: page.badgeLog };
    }
    for (const [label, value] of Object.entries(InvalidBadges)) {
      const page = loadPage("ok");
      const answer = await callHelper(page, value);
      result.invalid[label] = { ...answer, calls: page.badgeLog };
    }
    for (const mode of ["missing", "throws", "rejects"]) {
      const set = loadPage(mode);
      const setAnswer = await callHelper(set, 5);
      const clear = loadPage(mode);
      const clearAnswer = await callHelper(clear, 0);
      result.modes[mode] = {
        set: { ...setAnswer, calls: set.badgeLog },
        clear: { ...clearAnswer, calls: clear.badgeLog },
      };
    }
    return result;
  },

  // AC-6: the helper's promise resolves to true only once the Badging call has settled.
  async pageSettles() {
    const page = loadPage("deferred");
    const promise = page.sandbox.setRoomKitBadge(9);
    const state = { value: "pending" };
    promise.then((value) => {
      state.value = "resolved:" + value;
    });
    await drain();
    const before = state.value;
    page.badgePending[0].resolve();
    await promise;
    await drain();
    return { before: before, after: state.value, calls: page.badgeLog };
  },
};

async function main() {
  const body = scenarios[scenario.name];
  if (typeof body !== "function") {
    throw new Error("unknown scenario " + scenario.name);
  }
  const result = await body();
  process.stdout.write(JSON.stringify(result) + "\n");
}

main().catch((error) => {
  process.stderr.write(String(error && error.stack ? error.stack : error) + "\n");
  process.exitCode = 1;
});
