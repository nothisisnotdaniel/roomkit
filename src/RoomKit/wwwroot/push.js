"use strict";

// Notifications section on the board view. Loaded after token.js (authFetch) and board.js, before app.js.
// Fixed texts only. The permission prompt is asked for only from the "Turn on" tap, as iOS requires.

const PushTexts = {
  loading: "Checking notifications…",
  unsupported: "This browser can't show notifications.",
  ...PushAppTexts,
  off: "Get a notification when a job needs you or new items wait for approval.",
  on: "Notifications are on for this device.",
  turnOn: "Turn on",
  turnOff: "Turn off",
  sendTest: "Send test",
  checkAgain: "Check again",
  notifyDone: "Notify when a job is done",
  notifyUpdate: "Notify when an update is available",
  failed: "Can't change notifications. Try again.",
  notAllowed: "Permission was not given.",
  testSent: "Test sent.",
  testNone: "No device got the test.",
  tooSoon: "Wait a few seconds before sending another test.",
};

let pushBusy = false;
let pushNote = "";
let pushRender = 0;

// "homeScreen" on iOS outside the Home Screen app (Safari has no push there), "unsupported" without the APIs, else "".
function pushSupport() {
  const ios = /iPhone|iPad|iPod/.test(navigator.userAgent)
    || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const standalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  if (ios && !standalone) {
    return "homeScreen";
  }
  if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
    return "unsupported";
  }
  return "";
}

function base64UrlBytes(text) {
  const padded = text.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((text.length + 3) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

function sameBytes(left, right) {
  if (!left || !right || left.byteLength !== right.byteLength) {
    return false;
  }
  const a = new Uint8Array(left);
  const b = new Uint8Array(right);
  return a.every((value, index) => value === b[index]);
}

async function pushJson(path, body) {
  const options = body === undefined
    ? { method: "POST", cache: "no-store" }
    : {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    };
  const response = await authFetch(path, options);
  if (!response.ok) {
    const error = new Error("status");
    error.status = response.status;
    throw error;
  }
  return response.json();
}

async function pushInfo() {
  const response = await authFetch("/api/push", { cache: "no-store" });
  if (!response.ok) {
    throw new Error("status");
  }
  const data = await response.json();
  if (!data || typeof data.publicKey !== "string") {
    throw new Error("data");
  }
  return data;
}

async function currentSubscription() {
  const registration = await navigator.serviceWorker.getRegistration("/");
  return registration ? registration.pushManager.getSubscription() : null;
}

function pushButton(label, onClick, primary) {
  const button = element("button", primary ? "edit-button edit-primary" : "edit-button", label);
  button.type = "button";
  button.disabled = pushBusy;
  button.addEventListener("click", onClick);
  return button;
}

function settingSwitch(text, checked, onChange) {
  const label = element("label", "push-switch");
  const input = document.createElement("input");
  input.type = "checkbox";
  input.setAttribute("role", "switch");
  input.checked = checked;
  input.disabled = pushBusy;
  input.addEventListener("change", () => onChange(input.checked));
  label.append(input, element("span", null, text));
  return label;
}

function doneSwitch(checked) {
  return settingSwitch(PushTexts.notifyDone, checked, setNotifyDone);
}

function updateSwitch(checked) {
  return settingSwitch(PushTexts.notifyUpdate, checked, setNotifyUpdate);
}

function showPush(text, controls) {
  document.getElementById("push-text").textContent = text;
  document.getElementById("push-actions").replaceChildren(...controls);
  const note = document.getElementById("push-note");
  note.textContent = pushNote;
  note.hidden = pushNote === "";
}

// Shows the state of this device: unsupported, homeScreen, denied, off or on.
async function renderPush() {
  const request = ++pushRender;
  const support = pushSupport();
  if (support !== "") {
    showPush(PushTexts[support], []);
    return;
  }
  if (Notification.permission === "denied") {
    showPush(PushTexts.denied, [pushButton(PushTexts.checkAgain, () => renderPush(), false)]);
    return;
  }
  let info = null;
  let subscription = null;
  try {
    info = await pushInfo();
    subscription = await currentSubscription();
    if (subscription && !sameBytes(subscription.options.applicationServerKey, base64UrlBytes(info.publicKey).buffer)) {
      // Made with an older server key: the push service would refuse our messages.
      await subscription.unsubscribe();
      subscription = null;
    }
    if (subscription && Notification.permission === "granted") {
      // Keeps the server's copy current (subscribing the same endpoint again replaces it).
      await pushJson("/api/push/subscribe", subscription.toJSON());
    }
  } catch {
    if (request === pushRender) {
      pushNote = PushTexts.failed;
      showPush(PushTexts.off, [pushButton(PushTexts.checkAgain, () => renderPush(), false)]);
    }
    return;
  }
  if (request !== pushRender) {
    return;
  }
  if (subscription && Notification.permission === "granted") {
    showPush(PushTexts.on, [
      doneSwitch(info.notifyDone === true),
      updateSwitch(info.notifyUpdate !== false),
      pushButton(PushTexts.sendTest, sendTestPush, false),
      pushButton(PushTexts.turnOff, turnOffPush, false),
    ]);
  } else {
    showPush(PushTexts.off, [pushButton(PushTexts.turnOn, turnOnPush, true)]);
  }
}

async function withPushBusy(work) {
  if (pushBusy) {
    return;
  }
  pushBusy = true;
  pushNote = "";
  try {
    await work();
  } catch {
    pushNote = PushTexts.failed;
  } finally {
    pushBusy = false;
    await renderPush();
  }
}

// The tap handler: the permission prompt comes first, still inside the tap, then the subscription with the
// server's key.
function turnOnPush() {
  if (pushBusy) {
    return;
  }
  const asked = Notification.requestPermission();
  withPushBusy(async () => {
    const permission = await asked;
    if (permission !== "granted") {
      pushNote = PushTexts.notAllowed;
      return;
    }
    const info = await pushInfo();
    await navigator.serviceWorker.register("/sw.js");
    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: base64UrlBytes(info.publicKey),
    });
    await pushJson("/api/push/subscribe", subscription.toJSON());
  });
}

function turnOffPush() {
  withPushBusy(async () => {
    const subscription = await currentSubscription();
    if (subscription) {
      const endpoint = subscription.endpoint;
      await subscription.unsubscribe();
      await pushJson("/api/push/unsubscribe", { endpoint: endpoint });
    }
  });
}

function sendTestPush() {
  withPushBusy(async () => {
    let result;
    try {
      result = await pushJson("/api/push/test");
    } catch (error) {
      if (error.status === 429) {
        pushNote = PushTexts.tooSoon;
        return;
      }
      throw error;
    }
    pushNote = result && result.sent > 0 ? PushTexts.testSent : PushTexts.testNone;
  });
}

function setNotifyDone(on) {
  withPushBusy(async () => {
    await pushJson("/api/push/settings", { notifyDone: on });
  });
}

function setNotifyUpdate(on) {
  withPushBusy(async () => {
    await pushJson("/api/push/settings", { notifyUpdate: on });
  });
}

// Called by board.js each time the board is shown.
function showPushSection() {
  if (pushBusy) {
    return;
  }
  pushNote = "";
  renderPush();
}
