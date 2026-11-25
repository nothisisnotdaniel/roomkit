importScripts("/room.js");
"use strict";

// Caches only the app shell. Backlog, story and job data are never cached, so an unreachable server shows the
// app's fixed text instead of old data. Bump the version when the shell list changes.

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CacheName).then((cache) => cache.addAll(Shell)));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((names) =>
      Promise.all(names.filter((name) => name !== CacheName).map((name) => caches.delete(name)))));
});

function isShellRequest(request) {
  if (request.method !== "GET") {
    return false;
  }
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) {
    return false;
  }
  if (url.pathname.startsWith("/api/") || url.pathname === "/health") {
    return false;
  }
  return Shell.includes(url.pathname);
}

async function staleWhileRevalidate(event) {
  // One entry per shell path: the key is the bare pathname, so a query string never adds an entry.
  const key = new URL(event.request.url).pathname;
  const cache = await caches.open(CacheName);
  const cached = await cache.match(key);
  const network = fetch(event.request).then(async (response) => {
    if (response.ok) {
      await cache.put(key, response.clone());
    }
    return response;
  });
  if (cached) {
    event.waitUntil(network.catch(() => undefined));
    return cached;
  }
  return network;
}

self.addEventListener("fetch", (event) => {
  if (!isShellRequest(event.request)) {
    return;
  }
  event.respondWith(staleWhileRevalidate(event));
});

// Push: the payload holds only kind, project, job, stage, version, previous, reason and count. The title comes from this fixed table,
// never from the payload; the body is built from the payload's names. Nothing is fetched here.
const PushTitles = {
  "needs-human": "Needs you",
  "merge-conflict": "Merge conflict",
  "ci-failed": "CI failed after merge",
  done: "Done",
  approval: "New items to approve",
  test: "Test notification",
  update: "Update available",
  ...PushAppTitles,
  update_failed: "Update failed",
};
// The fixed body per updater failure code (the payload's reason); an unknown code has no body.
const UpdateFailedBodies = {
  check: "The download didn't check out. Nothing was changed.",
  no_answer: "The new version didn't start, so the old one is back.",
  swap: "The new version couldn't be put in place, so the old one is back.",
  bootstrap_failed: "The new version couldn't be started, so the old one is back.",
  rollback_failed: "The old version didn't restart. Check the computer running the server.",
};
const PushJobPattern = /^[0-9]{1,6}-[a-z0-9-]{1,80}(\.[0-9a-f]{32})?$/;
const PushVersionPattern = /^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z-]{1,40}(\.[0-9A-Za-z-]{1,40}){0,9})?$/;

function pushField(data, name) {
  const value = data[name];
  return typeof value === "string" ? value.slice(0, 120) : "";
}

function readPushData(event) {
  try {
    const data = event.data ? event.data.json() : null;
    return data && typeof data === "object" ? data : {};
  } catch {
    return {};
  }
}

function pushVersion(data, name) {
  return PushVersionPattern.test(pushField(data, name)) ? pushField(data, name) : "";
}

function pushNotification(data) {
  const kind = Object.prototype.hasOwnProperty.call(PushTitles, data.kind) ? data.kind : "";
  const project = pushField(data, "project");
  const job = PushJobPattern.test(pushField(data, "job")) ? pushField(data, "job") : "";
  const count = Number.isInteger(data.count) && data.count > 0 ? data.count : 0;
  let body = "";
  let tag = PushDefaultTag;
  let url = "/#jobs";
  if (kind === "approval") {
    body = (count === 1 ? "1 new item" : count + " new items") + (project ? " in " + project : "");
    tag = "approval:" + project;
    url = "/#backlogs";
  } else if (kind === "update") {
    const version = PushVersionPattern.test(pushField(data, "version")) ? pushField(data, "version") : "";
    body = version ? PushUpdatePrefix + version : "";
    tag = "update:" + version;
  } else if (kind === "updated") {
    const version = pushVersion(data, "version");
    const previous = pushVersion(data, "previous");
    body = version ? "Now " + version + (previous ? " (was " + previous + ")." : ".") : "";
    tag = "updated:" + version;
  } else if (kind === "update_failed") {
    const reason = pushField(data, "reason");
    body = Object.prototype.hasOwnProperty.call(UpdateFailedBodies, reason) ? UpdateFailedBodies[reason] : "";
    tag = "update_failed:" + pushVersion(data, "version");
  } else if (kind === "test") {
    body = "Notifications work on this device.";
    tag = "test:";
  } else if (job) {
    body = project ? project + " · " + job : job;
    tag = kind + ":" + job;
    url = "/#job/" + job;
  }
  return {
    title: kind ? PushTitles[kind] : PushFallbackTitle,
    options: { body: body, tag: tag, data: { url: url }, icon: "/icons/icon-192.png" },
  };
}

self.addEventListener("push", (event) => {
  const note = pushNotification(readPushData(event));
  event.waitUntil(self.registration.showNotification(note.title, note.options));
});

// Opens the job page (#job/<id>) or the backlogs view; focuses an open window of the app when there is one.
self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const data = event.notification.data;
  const path = data && typeof data.url === "string" && data.url.startsWith("/#") ? data.url : "/#jobs";
  const url = new URL(path, self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).origin === self.location.origin && "focus" in client) {
        try {
          const moved = await client.navigate(url);
          await (moved || client).focus();
          return;
        } catch {
          // Not controlled by this worker: open a new window below.
        }
      }
    }
    await self.clients.openWindow(url);
  })());
});
