"use strict";

// Runs the EditRowScriptTests checks: loads the real edit.js into a node:vm context with stub globals (document,
// element, confirm, authFetch and the app.js helpers it calls), builds the edit row of a [?], [ ], [h] and [x] line,
// clicks Drop with confirm answering false and then true, and prints the buttons, the confirm texts, the requests
// and the edit notice text as one JSON line. Usage: node EditRowHarness.js <scenario.json>, where the file holds
// { "wwwroot": "<folder>" }. No timer and no network: authFetch answers from a script, and every wait is on the
// promise changeAndReload returned.

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const scenario = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));

function fakeElement(tag, className, text) {
  const item = {
    tag: tag,
    id: null,
    hidden: false,
    type: "",
    className: className || "",
    textContent: text || "",
    children: [],
    attributes: {},
    listeners: {},
    appendChild(child) {
      item.children.push(child);
      return child;
    },
    append(...children) {
      item.children.push(...children);
    },
    setAttribute(name, value) {
      item.attributes[name] = String(value);
    },
    addEventListener(name, listener) {
      (item.listeners[name] = item.listeners[name] || []).push(listener);
    },
    click() {
      for (const listener of item.listeners.click || []) {
        listener({ stopPropagation() {} });
      }
    },
  };
  item.classList = {
    toggle(name, on) {
      const names = item.className.split(" ").filter((part) => part !== "" && part !== name);
      if (on !== false) {
        names.push(name);
      }
      item.className = names.join(" ");
    },
    add(name) {
      item.classList.toggle(name, true);
    },
    remove(name) {
      item.classList.toggle(name, false);
    },
    contains(name) {
      return item.className.split(" ").includes(name);
    },
  };
  return item;
}

const elements = new Map();
const confirms = [];
const requests = [];
const answers = [];
let confirmAnswer = false;

const sandbox = {
  console: console,
  JSON: JSON,
  document: {
    body: fakeElement("body"),
    getElementById(id) {
      if (!elements.has(id)) {
        const item = fakeElement("div");
        item.id = id;
        elements.set(id, item);
      }
      return elements.get(id);
    },
  },
  element: fakeElement,
  asText: (value) => (typeof value === "string" ? value : ""),
  asNumber: (value) => (typeof value === "number" && Number.isInteger(value) ? value : null),
  titleText: (value) => (typeof value === "string" ? value : ""),
  confirm: (text) => {
    confirms.push(text);
    return confirmAnswer;
  },
  authFetch: (url, options) => {
    requests.push({ url: url, method: options.method, body: JSON.parse(options.body) });
    const answer = answers.shift();
    if (!answer) {
      throw new Error("no scripted answer");
    }
    return Promise.resolve({ ok: answer.status === 200, status: answer.status, json: () => Promise.resolve(answer.body) });
  },
  tokenViewShown: () => false,
  loadBacklogCounts: () => {},
  load: () => {},
};

const context = vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(path.join(scenario.wwwroot, "edit.js"), "utf8"), context, { filename: "edit.js" });

// editRow calls changeAndReload by its global name, so the wrapper records the promise each click starts.
const pending = [];
const original = vm.runInContext("changeAndReload", context);
context.changeAndReload = (body) => {
  const promise = original(body);
  pending.push(promise);
  return promise;
};
const run = (code) => vm.runInContext(code, context);
const texts = run("({ confirmDrop: EditTexts.confirmDrop, confirmDropApproved: EditTexts.confirmDropApproved, "
  + "waitedOn: ChangeErrors.waited_on })");

function entry(mark) {
  return { mark: mark, line: 4, number: 7, title: "Seven" };
}

function row(mark) {
  return context.editRow("Alpha", "v1", entry(mark));
}

function buttons(item) {
  return item === null ? null : item.children.map((button) => ({
    text: button.textContent,
    label: button.attributes["aria-label"] || "",
    className: button.className,
  }));
}

// Clicks Drop on a fresh row of the mark with the confirm answer and waits for the change it started, if any.
async function clickDrop(mark, answer, reply) {
  confirmAnswer = answer;
  const start = { confirms: confirms.length, requests: requests.length, pending: pending.length };
  if (reply) {
    answers.push(reply);
  }
  const drop = row(mark).children.find((button) => button.textContent === "Drop");
  drop.click();
  await Promise.all(pending.slice(start.pending));
  return {
    confirms: confirms.slice(start.confirms),
    requests: requests.slice(start.requests),
    notice: elements.has("edit-notice-text") ? elements.get("edit-notice-text").textContent : "",
  };
}

(async () => {
  const result = {
    texts: texts,
    rows: { proposed: buttons(row("?")), approved: buttons(row(" ")), user: buttons(row("h")), done: buttons(row("x")) },
    approvedNo: await clickDrop(" ", false, null),
    proposedNo: await clickDrop("?", false, null),
    proposedYes: await clickDrop("?", true, { status: 200, body: { committed: true, pushed: false, version: "v2" } }),
    approvedYes: await clickDrop(" ", true, { status: 409, body: { error: "waited_on", detail: "server text" } }),
  };
  result.noticeError = elements.get("edit-notice").classList.contains("edit-notice-error");
  process.stdout.write(JSON.stringify(result) + "\n");
})().catch((error) => {
  process.stderr.write(String(error && error.stack ? error.stack : error) + "\n");
  process.exit(1);
});
