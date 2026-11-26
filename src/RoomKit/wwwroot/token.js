"use strict";

// The access token: kept in localStorage on this device, sent only as an Authorization: Bearer header on API
// calls (never in a URL). Without a token, or when the server refuses it, the token view replaces the data views.


const TokenTexts = {
  refused: "The token was refused. Enter the current token.",
};

let tokenSaved = null;

function readToken() {
  try {
    return localStorage.getItem(TokenKey) || "";
  } catch {
    return "";
  }
}

function storeToken(token) {
  try {
    localStorage.setItem(TokenKey, token);
  } catch {
    // Storage off (private mode): the token works until the page is closed only.
  }
}

function forgetToken() {
  try {
    localStorage.removeItem(TokenKey);
  } catch {
    // Nothing stored.
  }
}

function hasToken() {
  return readToken() !== "";
}

function tokenViewShown() {
  return !document.getElementById("token").hidden;
}

// Hides every other view, the header and the tab bar, and shows the token form; refused adds the fixed refusal text.
// A second call while the view is shown (several requests refused at once) keeps what the user is typing.
function showTokenView(refused) {
  const note = document.getElementById("token-refused");
  if (refused) {
    note.textContent = TokenTexts.refused;
    note.hidden = false;
  }
  if (tokenViewShown()) {
    return;
  }
  if (!refused) {
    note.textContent = "";
    note.hidden = true;
  }
  for (const main of document.querySelectorAll("main")) {
    main.hidden = main.id !== "token";
  }
  document.getElementById("top").hidden = true;
  document.querySelector(".tab-bar").hidden = true;
  document.body.classList.remove("view-board");
  const input = document.getElementById("token-input");
  input.value = "";
  input.focus({ preventScroll: true });
}

function hideTokenView() {
  document.getElementById("token").hidden = true;
  document.getElementById("token-refused").hidden = true;
}

// Called once by app.js with the function that reloads the current view after Save.
function onTokenSaved(reload) {
  tokenSaved = reload;
}

function saveToken(event) {
  event.preventDefault();
  const input = document.getElementById("token-input");
  const token = input.value.trim();
  if (token === "") {
    input.focus({ preventScroll: true });
    return;
  }
  storeToken(token);
  input.value = "";
  hideTokenView();
  if (tokenSaved) {
    tokenSaved();
  }
}

// fetch for /api/ paths with the stored token as a Bearer header. With no token, or on 401, the stored token is
// removed, the token view is shown and the call fails, so callers keep their own fixed error handling.
async function authFetch(path, options) {
  const token = readToken();
  if (token === "") {
    showTokenView(false);
    throw new Error("token");
  }
  const headers = new Headers((options && options.headers) || {});
  headers.set("Authorization", "Bearer " + token);
  const response = await fetch(path, Object.assign({}, options, { headers: headers }));
  if (response.status === 401) {
    forgetToken();
    showTokenView(true);
    throw new Error("unauthorized");
  }
  return response;
}

document.getElementById("token-form").addEventListener("submit", saveToken);
