"use strict";

// Backlog editing: per-project edit mode (approve, drop, move by drag or arrow keys, to top, edit title, add, the auto
// stop line), the story editor, Add note, the story's Category select and Done of a [h] story. Every change
// is one POST to /api/backlog/change through authFetch with the version the view loaded. Only fixed texts are shown;
// server text never is. Uses element() and the views' reload functions from app.js at call time.

const EditTexts = {
  edit: "Edit",
  doneEditing: "Done",
  approve: "Approve",
  drop: "Drop",
  markDone: "Done",
  toTop: "To top",
  move: "Move",
  moveHint: "Use arrow keys to move within the group.",
  editTitle: "Edit title",
  save: "Save",
  cancel: "Cancel",
  add: "Add item",
  addButton: "Add",
  titleLabel: "Title",
  whatLabel: "What",
  whyLabel: "Why",
  whereLabel: "Where",
  top: "Top",
  bottom: "Bottom",
  editStory: "Edit story",
  storyLabel: "Story (Markdown)",
  addNote: "Add note",
  noteLabel: "Note",
  noteHint: "Saved as one line under Notes, with today's date.",
  category: "Category",
  noCategory: "No category",
  dismiss: "Close",
  confirmDrop: "Drop this proposal? Its line and story file are removed in one commit.",
  moveStop: "Move stop line",
  removeStop: "Remove stop line",
  stopAfter: "Stop after this",
  addStop: "Add stop line",
  confirmRemoveStop: "Remove the stop line? Auto then runs on through the whole Open list.",
  saved: "Saved.",
  savedPushed: "Saved and pushed.",
};

// One fixed text per error code; anything else gets the generic text.
const ChangeErrors = {
  not_develop: "Not on develop. Nothing was changed.",
  dirty: "Backlog has uncommitted changes.",
  changed: "Backlog changed since you loaded it. Reload and try again.",
  invalid: "This change isn't allowed. Nothing was changed.",
  commit_failed: "The commit failed. Nothing was changed.",
  write_failed: "Can't write the file. Nothing was changed.",
  push_failed: "Committed, but the push failed.",
  worktree_failed: "Could not prepare the backlog branch. Nothing was changed.",
  no_worktree: "The factory's develop worktree is missing. Nothing was changed.",
  stop_line: "This backlog has more than one stop line, or one with other text. Fix backlog.md first.",
  unreachable: "Can't reach the server. Reload to see whether the change was saved.",
  notEditable: "This story can't be edited.",
  failed: "The change failed. Reload to see whether it was saved.",
};

// Projects in edit mode survive a reload of the list.
const editingProjects = new Set();
let changeBusy = false;

function showEditNotice(text, isError) {
  const notice = document.getElementById("edit-notice");
  const message = document.getElementById("edit-notice-text");
  message.textContent = text;
  notice.classList.toggle("edit-notice-error", isError === true);
  notice.hidden = false;
}

function hideEditNotice() {
  document.getElementById("edit-notice").hidden = true;
}

function editButton(label, className, onClick) {
  const button = element("button", "edit-button" + (className ? " " + className : ""), label);
  button.type = "button";
  button.addEventListener("click", (event) => {
    event.stopPropagation();
    onClick(event);
  });
  return button;
}

// Sends one change. Returns true when it was committed. Shows the fixed text for the outcome.
async function sendChange(body) {
  if (changeBusy) {
    return false;
  }
  changeBusy = true;
  document.body.classList.add("edit-busy");
  hideEditNotice();
  let response;
  let data = null;
  try {
    response = await authFetch("/api/backlog/change", {
      method: "POST",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    try {
      data = await response.json();
    } catch {
      data = null;
    }
  } catch {
    changeBusy = false;
    document.body.classList.remove("edit-busy");
    if (!tokenViewShown()) {
      showEditNotice(ChangeErrors.unreachable, true);
    }
    return false;
  }
  changeBusy = false;
  document.body.classList.remove("edit-busy");
  if (response.ok && data && data.committed === true) {
    if (data.warning === "push_failed") {
      showEditNotice(ChangeErrors.push_failed, true);
    } else {
      showEditNotice(data.pushed === true ? EditTexts.savedPushed : EditTexts.saved, false);
    }
    // Every committed change can move a [h] or [?] line, so the counts on the Backlogs tab are fetched again.
    loadBacklogCounts();
    return true;
  }
  const code = data && typeof data.error === "string" ? data.error : "";
  showEditNotice(Object.prototype.hasOwnProperty.call(ChangeErrors, code) ? ChangeErrors[code] : ChangeErrors.failed,
    true);
  return false;
}

async function changeAndReload(body) {
  if (await sendChange(body)) {
    load();
  }
}

// The Edit / Done toggle in a project's heading row.
function editToggle(projectName, section) {
  const button = editButton(EditTexts.edit, "edit-toggle", () => {
    const on = !editingProjects.has(projectName);
    if (on) {
      editingProjects.add(projectName);
    } else {
      editingProjects.delete(projectName);
    }
    applyEditMode(section, button, on);
  });
  applyEditMode(section, button, editingProjects.has(projectName));
  return button;
}

function applyEditMode(section, button, on) {
  section.classList.toggle("editing", on);
  button.textContent = on ? EditTexts.doneEditing : EditTexts.edit;
  button.setAttribute("aria-pressed", on ? "true" : "false");
}

// The action row of one line (shown in edit mode only), or null for a done line.
function editRow(projectName, version, entry) {
  const mark = asText(entry && entry.mark);
  const line = asNumber(entry && entry.line);
  if (mark === "x" || line === null || version === "") {
    return null;
  }
  const number = asNumber(entry.number);
  // Screen readers hear which item each button acts on, not the same names on every line.
  const subject = number !== null ? "#" + number : titleText(entry.title);
  const named = (button) => {
    button.setAttribute("aria-label", button.textContent + " " + subject);
    return button;
  };
  const row = element("div", "edit-row edit-only");
  const send = (fields) => changeAndReload(Object.assign({ project: projectName, version: version, line: line }, fields));
  if (mark === "?") {
    row.appendChild(named(editButton(EditTexts.approve, "edit-primary", () => send({ action: "approve" }))));
    row.appendChild(named(editButton(EditTexts.drop, "edit-danger", () => {
      if (confirm(EditTexts.confirmDrop)) {
        send({ action: "drop" });
      }
    })));
  }
  row.appendChild(named(editButton(EditTexts.toTop, null, () => send({ action: "top" }))));
  if (number !== null) {
    row.appendChild(named(editButton(EditTexts.editTitle, null, () => openTitleForm(row, entry, send))));
  }
  row.addEventListener("click", (event) => event.stopPropagation());
  return row;
}

// Drag to reorder: a handle at the start of every [ ] and [?] row (edit mode only). A pointer drag (pointer events
// only, captured on the handle) or ArrowUp/ArrowDown on the focused handle sends one "place" change; the item only
// ever lands among the rows of its own group's list. No HTML5 drag and drop, no timers: auto-scroll is stepped by
// requestAnimationFrame.
const DragEdge = 48;
const DragScrollStep = 12;
let dragBusy = false;
let dragState = null;
let dragHintCount = 0;

// The handle and its screen reader hint for one line, or null for a line that cannot be placed.
function dragHandle(projectName, version, entry) {
  const mark = asText(entry && entry.mark);
  const line = asNumber(entry && entry.line);
  if ((mark !== " " && mark !== "?") || line === null || version === "") {
    return null;
  }
  const number = asNumber(entry.number);
  const subject = number !== null ? "#" + number : titleText(entry.title);
  const handle = element("button", "drag-handle edit-only", "≡");
  handle.type = "button";
  handle.setAttribute("aria-label", EditTexts.move + " " + subject);
  dragHintCount += 1;
  const hint = element("span", "visually-hidden drag-hint", EditTexts.moveHint);
  hint.id = "drag-hint-" + dragHintCount;
  handle.setAttribute("aria-describedby", hint.id);
  handle.dataset.line = String(line);
  if (number !== null) {
    handle.dataset.number = String(number);
  }
  const place = { project: projectName, version: version };
  // A tap or a drag on the handle never opens the story: its click is swallowed.
  handle.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
  });
  handle.addEventListener("pointerdown", (event) => startDrag(event, handle, place));
  handle.addEventListener("pointermove", (event) => moveDrag(event));
  handle.addEventListener("pointerup", (event) => endDrag(event, true));
  handle.addEventListener("pointercancel", (event) => endDrag(event, false));
  handle.addEventListener("lostpointercapture", (event) => endDrag(event, false));
  handle.addEventListener("keydown", (event) => keyMove(event, handle, place));
  return [handle, hint];
}

// The rows of the handle's group, in file order: the list items of its own list that have a handle.
function dragRows(list) {
  return [...list.children].filter((row) => row.querySelector(":scope > .drag-handle") !== null);
}

function rowLine(row) {
  return Number(row.querySelector(":scope > .drag-handle").dataset.line);
}

function startDrag(event, handle, place) {
  if (dragBusy || changeBusy || !event.isPrimary || (event.pointerType === "mouse" && event.button !== 0)) {
    return;
  }
  const row = handle.closest("li");
  const list = row && row.parentElement;
  if (!list) {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  handle.setPointerCapture(event.pointerId);
  const rows = dragRows(list);
  const indicator = element("li", "drop-indicator");
  indicator.setAttribute("aria-hidden", "true");
  indicator.hidden = true;
  list.classList.add("drag-list");
  list.appendChild(indicator);
  row.classList.add("dragging");
  dragBusy = true;
  dragState = {
    pointerId: event.pointerId,
    handle: handle,
    place: place,
    row: row,
    list: list,
    rows: rows,
    original: rows.indexOf(row),
    indicator: indicator,
    x: event.clientX,
    y: event.clientY,
    drop: null,
    frame: 0,
  };
  document.addEventListener("keydown", dragKey, true);
  dragState.frame = requestAnimationFrame(dragFrame);
  showDrop();
}

function moveDrag(event) {
  if (dragState === null || event.pointerId !== dragState.pointerId) {
    return;
  }
  event.preventDefault();
  dragState.x = event.clientX;
  dragState.y = event.clientY;
  showDrop();
}

// One auto-scroll step per frame while the pointer is in the top or bottom 48 px of the viewport.
function dragFrame() {
  if (dragState === null) {
    return;
  }
  let step = 0;
  if (dragState.y < DragEdge) {
    step = -DragScrollStep;
  } else if (dragState.y > window.innerHeight - DragEdge) {
    step = DragScrollStep;
  }
  if (step !== 0) {
    window.scrollBy(0, step);
    showDrop();
  }
  dragState.frame = requestAnimationFrame(dragFrame);
}

// Where the row would land now: the index among the other rows of the group, or null outside the list.
function dropPlace() {
  const state = dragState;
  const box = state.list.getBoundingClientRect();
  if (state.x < box.left || state.x > box.right || state.y < box.top || state.y > box.bottom) {
    return null;
  }
  const others = state.rows.filter((row) => row !== state.row);
  let index = 0;
  for (const row of others) {
    const rect = row.getBoundingClientRect();
    if (rect.top + rect.height / 2 < state.y) {
      index += 1;
    }
  }
  return { index: index, others: others };
}

function showDrop() {
  const state = dragState;
  const drop = dropPlace();
  state.drop = drop;
  if (drop === null || drop.others.length === 0) {
    state.indicator.hidden = true;
    return;
  }
  const at = drop.index === state.original
    ? state.row.getBoundingClientRect().top
    : drop.index < drop.others.length
      ? drop.others[drop.index].getBoundingClientRect().top
      : drop.others[drop.others.length - 1].getBoundingClientRect().bottom;
  state.indicator.style.top = Math.round(at - state.list.getBoundingClientRect().top) + "px";
  state.indicator.hidden = false;
}

// Escape cancels a drag; nothing is sent.
function dragKey(event) {
  if (event.key === "Escape" && dragState !== null) {
    event.preventDefault();
    event.stopPropagation();
    cancelDrag();
  }
}

// Ends the drag in progress and returns its state, or null when there is none.
function cancelDrag() {
  const state = dragState;
  if (state === null) {
    return null;
  }
  dragState = null;
  dragBusy = false;
  cancelAnimationFrame(state.frame);
  document.removeEventListener("keydown", dragKey, true);
  state.indicator.remove();
  state.list.classList.remove("drag-list");
  state.row.classList.remove("dragging");
  if (state.handle.hasPointerCapture(state.pointerId)) {
    state.handle.releasePointerCapture(state.pointerId);
  }
  return state;
}

// A release inside the list at a new place sends one place change; anything else (the original place, outside the
// list, pointercancel, a lost capture) only ends the drag.
function endDrag(event, release) {
  if (dragState === null || event.pointerId !== dragState.pointerId) {
    return;
  }
  if (release) {
    dragState.x = event.clientX;
    dragState.y = event.clientY;
    dragState.drop = dropPlace();
  }
  const state = cancelDrag();
  const drop = state.drop;
  if (!release || drop === null || drop.index === state.original || drop.others.length === 0) {
    return;
  }
  const before = drop.index === 0;
  const target = before ? drop.others[0] : drop.others[drop.index - 1];
  if (state.place.stop === true) {
    sendStopMove(state.place, rowLine(state.row), rowLine(target), before ? "before" : "after", null);
    return;
  }
  sendPlace(state.place, rowLine(state.row), rowLine(target), before ? "before" : "after", null);
}

// ArrowUp / ArrowDown on the focused handle: before the previous or after the next item of the group, nothing at the
// ends; focus comes back to this item's handle after the reload.
function keyMove(event, handle, place) {
  if (event.key !== "ArrowUp" && event.key !== "ArrowDown") {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  if (dragBusy || changeBusy) {
    return;
  }
  const row = handle.closest("li");
  const rows = row && row.parentElement ? dragRows(row.parentElement) : [];
  const index = rows.indexOf(row);
  const up = event.key === "ArrowUp";
  const other = up ? index - 1 : index + 1;
  if (index < 0 || other < 0 || other >= rows.length) {
    return;
  }
  // Swapping with a neighbour keeps the group's lines, so the moved item lands on the neighbour's line.
  const line = rowLine(row);
  const target = rowLine(rows[other]);
  if (place.stop === true) {
    sendStopMove(place, line, target, up ? "before" : "after", { stop: true });
    return;
  }
  const focus = { number: handle.dataset.number || null, line: target };
  sendPlace(place, line, target, up ? "before" : "after", focus);
}

async function sendPlace(place, line, target, where, focus) {
  const ok = await sendChange({
    project: place.project,
    version: place.version,
    action: "place",
    line: line,
    target: target,
    where: where,
  });
  if (!ok) {
    return;
  }
  await load();
  if (focus !== null) {
    focusHandle(place.project, focus);
  }
}

// The stop line moved by a drop or an arrow key: one stop_move, then the reload; the stop text is the server's.
async function sendStopMove(place, line, target, where, focus) {
  const ok = await sendChange({
    project: place.project,
    version: place.version,
    action: "stop_move",
    line: line,
    target: target,
    where: where,
  });
  if (!ok) {
    return;
  }
  await load();
  if (focus !== null) {
    focusHandle(place.project, focus);
  }
}

// The moved item's handle in the reloaded section: by number, else (an unnumbered line) by its new line; the stop
// line's own handle for a moved stop line.
function focusHandle(projectName, focus) {
  const section = [...document.querySelectorAll("#backlogs section.project")]
    .find((node) => node.dataset.project === projectName);
  if (!section) {
    return;
  }
  if (focus.stop === true) {
    const stop = section.querySelector(".stop-line > .drag-handle");
    if (stop) {
      stop.focus();
    }
    return;
  }
  const handle = [...section.querySelectorAll(".drag-handle")].find((node) => focus.number !== null
    ? node.dataset.number === focus.number
    : node.dataset.line === String(focus.line));
  if (handle) {
    handle.focus();
  }
}

// The stop line's edit controls (edit mode only): a handle like an item's (drag or ArrowUp/ArrowDown sends one
// stop_move to the neighbouring item row; Escape and pointercancel send nothing) and Remove after a confirm.
function appendStopEdit(item, projectName, version, line) {
  if (version === "") {
    return;
  }
  const handle = element("button", "drag-handle edit-only", "≡");
  handle.type = "button";
  handle.setAttribute("aria-label", EditTexts.moveStop);
  dragHintCount += 1;
  const hint = element("span", "visually-hidden drag-hint", EditTexts.moveHint);
  hint.id = "drag-hint-" + dragHintCount;
  handle.setAttribute("aria-describedby", hint.id);
  handle.dataset.line = String(line);
  const place = { project: projectName, version: version, stop: true };
  handle.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
  });
  handle.addEventListener("pointerdown", (event) => startDrag(event, handle, place));
  handle.addEventListener("pointermove", (event) => moveDrag(event));
  handle.addEventListener("pointerup", (event) => endDrag(event, true));
  handle.addEventListener("pointercancel", (event) => endDrag(event, false));
  handle.addEventListener("lostpointercapture", (event) => endDrag(event, false));
  handle.addEventListener("keydown", (event) => keyMove(event, handle, place));
  item.prepend(handle, hint);
  const row = element("div", "edit-row edit-only");
  row.appendChild(editButton(EditTexts.removeStop, "edit-danger stop-remove-button", () => {
    if (confirm(EditTexts.confirmRemoveStop)) {
      changeAndReload({ project: projectName, version: version, action: "stop_remove", line: line });
    }
  }));
  item.appendChild(row);
}

// Stop after this, in the edit row of a [ ] row while the backlog has no stop line: the line goes directly after it.
function appendStopAfter(item, projectName, version, entry) {
  const line = asNumber(entry && entry.line);
  const row = item ? item.querySelector(":scope > .edit-row") : null;
  if (asText(entry && entry.mark) !== " " || line === null || version === "" || row === null) {
    return;
  }
  const number = asNumber(entry.number);
  const subject = number !== null ? "#" + number : titleText(entry.title);
  const button = editButton(EditTexts.stopAfter, "stop-after-button", () => {
    changeAndReload({ project: projectName, version: version, action: "stop_add", line: line });
  });
  button.setAttribute("aria-label", EditTexts.stopAfter + " " + subject);
  row.appendChild(button);
}

// Add stop line under the Open list while the backlog has none: the line goes after the first [ ] row. null when
// there is no [ ] row.
function addStopButton(projectName, version, entries) {
  const first = entries.find((entry) => asText(entry && entry.mark) === " " && asNumber(entry.line) !== null);
  if (version === "" || first === undefined) {
    return null;
  }
  const row = element("div", "edit-row edit-only stop-add-row");
  row.appendChild(editButton(EditTexts.addStop, "stop-add-button", () => {
    changeAndReload({ project: projectName, version: version, action: "stop_add", line: first.line });
  }));
  return row;
}

function openTitleForm(row, entry, send) {
  if (row.querySelector(".edit-title-form")) {
    return;
  }
  const form = element("form", "edit-form edit-title-form");
  form.noValidate = true;
  const input = element("input", "edit-input");
  input.type = "text";
  input.maxLength = 200;
  input.value = asText(entry.title);
  input.setAttribute("aria-label", EditTexts.titleLabel);
  const actions = element("div", "edit-actions");
  const save = element("button", "edit-button edit-primary", EditTexts.save);
  save.type = "submit";
  const cancel = editButton(EditTexts.cancel, null, () => form.remove());
  actions.append(save, cancel);
  form.append(input, actions);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    send({ action: "title", title: input.value });
  });
  row.appendChild(form);
  input.focus();
}

function labelled(labelText, control) {
  const label = element("label", "edit-label");
  label.appendChild(element("span", null, labelText));
  label.appendChild(control);
  return label;
}

// The Add form of a project (edit mode only).
function addForm(projectName, version) {
  if (version === "") {
    return null;
  }
  const details = element("details", "edit-add edit-only");
  details.appendChild(element("summary", null, EditTexts.add));
  const form = element("form", "edit-form");
  form.noValidate = true;
  const title = element("input", "edit-input");
  title.type = "text";
  title.maxLength = 200;
  const what = element("textarea", "edit-input");
  what.rows = 3;
  const why = element("textarea", "edit-input");
  why.rows = 3;
  const where = element("select", "edit-input");
  for (const [value, text] of [["top", EditTexts.top], ["bottom", EditTexts.bottom]]) {
    const option = element("option", null, text);
    option.value = value;
    where.appendChild(option);
  }
  const add = element("button", "edit-button edit-primary", EditTexts.addButton);
  add.type = "submit";
  form.append(
    labelled(EditTexts.titleLabel, title),
    labelled(EditTexts.whatLabel, what),
    labelled(EditTexts.whyLabel, why),
    labelled(EditTexts.whereLabel, where),
    add);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    changeAndReload({
      project: projectName,
      version: version,
      action: "add",
      where: where.value,
      title: title.value,
      what: what.value,
      why: why.value,
    });
  });
  details.appendChild(form);
  return details;
}

// Finds the line, the backlog version, the group (open, approval, user or done), the mark and the position (1-based
// index in the group, the number the backlog list shows in front of the row, #108) of a numbered item, as the server
// sees it now.
async function findStoryEntry(project, number) {
  const response = await authFetch("/api/backlogs", { cache: "no-store" });
  if (!response.ok) {
    throw new Error("status");
  }
  const data = await response.json();
  const match = asList(data && data.projects).find((item) => asText(item && item.name) === project);
  if (!match || typeof match.version !== "string") {
    return null;
  }
  for (const group of ["open", "approval", "user", "done"]) {
    const entries = asList(match[group]);
    for (let index = 0; index < entries.length; index += 1) {
      const entry = entries[index];
      if (asNumber(entry && entry.number) === number && asNumber(entry.line) !== null) {
        return { version: match.version, line: entry.line, group: group, mark: asText(entry.mark),
          position: index + 1 };
      }
    }
  }
  return null;
}

// The not-done part of findStoryEntry: a done line counts as not found.
function notDone(entry) {
  if (entry === null || entry.group === "done") {
    return null;
  }
  return { version: entry.version, line: entry.line, group: entry.group };
}

// Finds the line, the backlog version and the group (open, approval or user) of a numbered item, as the server sees it now.
async function findStoryLine(project, number) {
  return notDone(await findStoryEntry(project, number));
}

// The Edit story button of the story view: opens a textarea with the raw Markdown, saved with storyVersion.
function storyEditButton(target, data, reload) {
  const markdown = asText(data && data.markdown);
  const storyVersion = asText(data && data.storyVersion);
  const number = asNumber(data && data.number);
  const button = editButton(EditTexts.editStory, "edit-story-button", async () => {
    const article = document.querySelector("#story .story");
    if (!article || storyFormOpen()) {
      return;
    }
    let found;
    try {
      found = await findStoryLine(target.project, number);
    } catch {
      if (!tokenViewShown()) {
        showEditNotice(ChangeErrors.unreachable, true);
      }
      return;
    }
    if (found === null || storyVersion === "") {
      showEditNotice(ChangeErrors.notEditable, true);
      return;
    }
    // A live refresh (live.js) may have replaced the story while the line was looked up.
    const shown = document.querySelector("#story .story");
    if (!shown || storyFormOpen()) {
      return;
    }
    const form = element("form", "edit-form edit-story-form");
    form.noValidate = true;
    const area = element("textarea", "edit-input edit-story-text");
    area.rows = 18;
    area.value = markdown;
    area.spellcheck = true;
    const actions = element("div", "edit-actions");
    const save = element("button", "edit-button edit-primary", EditTexts.save);
    save.type = "submit";
    const cancel = editButton(EditTexts.cancel, null, () => {
      form.replaceWith(shown);
      showStoryButtons(true);
    });
    actions.append(save, cancel);
    form.append(labelled(EditTexts.storyLabel, area), actions);
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      const ok = await sendChange({
        project: target.project,
        version: found.version,
        action: "story",
        line: found.line,
        storyVersion: storyVersion,
        markdown: area.value,
      });
      if (ok) {
        reload();
      }
    });
    shown.replaceWith(form);
    showStoryButtons(false);
    area.focus({ preventScroll: true });
  });
  if (number === null) {
    button.hidden = true;
  }
  return button;
}

// True while Edit story or Add note is open in the story view.
function storyFormOpen() {
  return document.querySelector("#story .edit-story-form, #story .add-note-form") !== null;
}

// Shows or hides the story bar buttons, so Edit story and Add note are never open at once and Approve, Drop and Done
// are never tapped while one of them is open.
function showStoryButtons(visible) {
  for (const button of document.querySelectorAll(
    "#story .edit-story-button, #story .add-note-button, #story .story-category, #story .story-approve-button, "
    + "#story .story-drop-button, #story .story-done-button")) {
    button.hidden = !visible;
  }
}

// The Add note button of the story view: a one-line note (Return is joined into the line by the server), dated by
// the server and appended under the story's Notes, saved with storyVersion.
function storyNoteButton(target, data, reload) {
  const storyVersion = asText(data && data.storyVersion);
  const number = asNumber(data && data.number);
  const button = editButton(EditTexts.addNote, "add-note-button", async () => {
    if (!document.querySelector("#story .story") || storyFormOpen()) {
      return;
    }
    let found;
    try {
      found = await findStoryLine(target.project, number);
    } catch {
      if (!tokenViewShown()) {
        showEditNotice(ChangeErrors.unreachable, true);
      }
      return;
    }
    if (found === null || storyVersion === "") {
      showEditNotice(ChangeErrors.notEditable, true);
      return;
    }
    // A live refresh (live.js) may have replaced the story while the line was looked up.
    const shown = document.querySelector("#story .story");
    if (!shown || storyFormOpen()) {
      return;
    }
    const form = element("form", "edit-form add-note-form");
    form.noValidate = true;
    const area = element("textarea", "edit-input add-note-text");
    area.rows = 4;
    area.maxLength = 1000;
    area.setAttribute("maxlength", "1000");
    area.spellcheck = true;
    const hint = element("p", "edit-hint", EditTexts.noteHint);
    hint.id = "add-note-hint";
    area.setAttribute("aria-describedby", hint.id);
    const actions = element("div", "edit-actions");
    const save = element("button", "edit-button edit-primary", EditTexts.save);
    save.type = "submit";
    const cancel = editButton(EditTexts.cancel, null, () => {
      form.replaceWith(shown);
      showStoryButtons(true);
    });
    // An empty note would only be refused, so Save waits for some text.
    const blank = () => area.value.trim() === "";
    save.disabled = true;
    area.addEventListener("input", () => {
      save.disabled = blank();
    });
    actions.append(save, cancel);
    form.append(labelled(EditTexts.noteLabel, area), hint, actions);
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (changeBusy || blank()) {
        return;
      }
      save.disabled = true;
      const ok = await sendChange({
        project: target.project,
        version: found.version,
        action: "note",
        line: found.line,
        storyVersion: storyVersion,
        note: area.value,
      });
      save.disabled = blank();
      if (ok) {
        reload();
      }
    });
    shown.replaceWith(form);
    showStoryButtons(false);
    area.focus({ preventScroll: true });
  });
  if (number === null) {
    button.hidden = true;
  }
  return button;
}

let storyDecisionRequest = 0;

// The status chip, the position, Approve and Drop in the story bar only while the item's backlog line is a [?]
// line, and Done only while it is a [h] line. Called after the story is shown and after a live refresh: buttons that
// should stay are kept as they are (focus stays on them).
async function showStoryDecision(target, data) {
  const number = asNumber(data && data.number);
  storyDecisionRequest += 1;
  const request = storyDecisionRequest;
  const story = storyRequest;
  let entry = null;
  if (number !== null) {
    try {
      entry = await findStoryEntry(target.project, number);
    } catch {
      entry = null;
    }
  }
  if (request !== storyDecisionRequest || story !== storyRequest || currentView !== "story") {
    return;
  }
  showStoryStatus(entry === null ? null : entry.mark);
  showStoryPosition(entry === null ? null : entry.position);
  const found = notDone(entry);
  const bar = document.querySelector("#story .story-bar");
  if (bar === null) {
    return;
  }
  showStoryCategory(bar, target, data, found);
  showStoryDone(bar, target, number, found);
  const shown = bar.querySelectorAll(".story-approve-button, .story-drop-button");
  if (found === null || found.group !== "approval") {
    for (const button of shown) {
      button.remove();
    }
    return;
  }
  if (shown.length === 0) {
    bar.append(...storyDecisionButtons(target, number));
  }
}

// The Category select in the story bar, only while the item is not done (found by findStoryLine). Built again from the
// shown story on every call, so it always carries the shown storyVersion; focus stays on it.
function showStoryCategory(bar, target, data, found) {
  const old = bar.querySelector(".story-category");
  if (found === null) {
    if (old !== null) {
      old.remove();
    }
    return;
  }
  const next = storyCategoryControl(target, data);
  if (old === null) {
    const decision = bar.querySelector(".story-approve-button, .story-done-button");
    bar.insertBefore(next, decision);
    return;
  }
  const focused = old.contains(document.activeElement);
  old.replaceWith(next);
  if (focused) {
    next.querySelector("select").focus({ preventScroll: true });
  }
}

// A labelled native select: the nine names, plus a disabled "No category" option chosen only while the story has
// none. Choosing a name sends action "category" through sendChange with the line looked up again and the shown
// storyVersion, then reloads the story.
function storyCategoryControl(target, data) {
  const storyVersion = asText(data && data.storyVersion);
  const number = asNumber(data && data.number);
  const current = typeof (data && data.category) === "string" && Categories.includes(data.category)
    ? data.category
    : "";
  const label = element("label", "story-category");
  label.appendChild(element("span", "story-category-label", EditTexts.category));
  const select = element("select", "story-category-select");
  if (current === "") {
    const none = element("option", null, EditTexts.noCategory);
    none.value = "";
    none.disabled = true;
    none.selected = true;
    select.appendChild(none);
  }
  for (const name of Categories) {
    const option = element("option", null, name);
    option.value = name;
    option.selected = name === current;
    select.appendChild(option);
  }
  select.disabled = changeBusy;
  select.addEventListener("click", (event) => event.stopPropagation());
  select.addEventListener("change", async () => {
    const chosen = select.value;
    if (changeBusy || storyFormOpen() || !Categories.includes(chosen) || chosen === current || number === null
      || storyVersion === "") {
      select.value = current;
      return;
    }
    select.disabled = true;
    let found;
    try {
      found = await findStoryLine(target.project, number);
    } catch {
      found = undefined;
    }
    if (found === undefined) {
      select.disabled = changeBusy;
      select.value = current;
      if (!tokenViewShown()) {
        showEditNotice(ChangeErrors.unreachable, true);
      }
      return;
    }
    if (found === null) {
      showEditNotice(ChangeErrors.changed, true);
      loadStory(target);
      return;
    }
    const ok = await sendChange({
      project: target.project,
      version: found.version,
      action: "category",
      line: found.line,
      storyVersion: storyVersion,
      category: chosen,
    });
    if (ok) {
      loadStory(target);
      return;
    }
    select.disabled = changeBusy;
    select.value = current;
  });
  label.appendChild(select);
  label.hidden = storyFormOpen();
  return label;
}

// The two buttons, named for screen readers by number; hidden while Edit story or Add note is open.
function storyDecisionButtons(target, number) {
  const subject = "#" + number;
  const approve = editButton(EditTexts.approve, "edit-primary story-approve-button", () => {
    decideStory(target, number, "approve");
  });
  approve.setAttribute("aria-label", EditTexts.approve + " " + subject);
  const drop = editButton(EditTexts.drop, "edit-danger story-drop-button", () => {
    if (confirm(EditTexts.confirmDrop)) {
      decideStory(target, number, "drop");
    }
  });
  drop.setAttribute("aria-label", EditTexts.drop + " " + subject);
  const hidden = storyFormOpen();
  approve.hidden = hidden;
  drop.hidden = hidden;
  return [approve, drop];
}

// Sends approve or drop with the version and line of the item as the server sees it at the tap, through sendChange.
// After either one succeeds the view closes (#128); a failed one stays on the story with its error.
async function decideStory(target, number, action) {
  if (changeBusy || storyFormOpen()) {
    return;
  }
  let found;
  try {
    found = await findStoryLine(target.project, number);
  } catch {
    if (!tokenViewShown()) {
      showEditNotice(ChangeErrors.unreachable, true);
    }
    return;
  }
  if (found === null || found.group !== "approval") {
    showEditNotice(ChangeErrors.changed, true);
    loadStory(target);
    return;
  }
  const ok = await sendChange({ project: target.project, version: found.version, line: found.line, action: action });
  if (!ok) {
    return;
  }
  leaveDecidedStory();
}

// Done in the story bar only while the item's line is a [h] line (group user), after Edit story, Add note and the
// Category select; a shown button is kept as it is (focus stays on it), else removed.
function showStoryDone(bar, target, number, found) {
  const old = bar.querySelector(".story-done-button");
  if (found === null || found.group !== "user") {
    if (old !== null) {
      old.remove();
    }
    return;
  }
  if (old === null) {
    bar.appendChild(storyDoneButton(target, number));
  }
}

// The Done button, named for screen readers by number; hidden while Edit story or Add note is open.
function storyDoneButton(target, number) {
  const done = editButton(EditTexts.markDone, "edit-primary story-done-button", () => {
    doneStory(target, number);
  });
  done.setAttribute("aria-label", EditTexts.markDone + " #" + number);
  done.hidden = storyFormOpen();
  return done;
}

// Sends done with the version and line of the [h] item as the server sees it at the tap, through sendChange. After it
// succeeds the view closes like Approve and Drop (#128); a failed one stays on the story with its error.
async function doneStory(target, number) {
  if (changeBusy || storyFormOpen()) {
    return;
  }
  let found;
  try {
    found = await findStoryLine(target.project, number);
  } catch {
    if (!tokenViewShown()) {
      showEditNotice(ChangeErrors.unreachable, true);
    }
    return;
  }
  if (found === null || found.group !== "user") {
    showEditNotice(ChangeErrors.changed, true);
    loadStory(target);
    return;
  }
  const ok = await sendChange({ project: target.project, version: found.version, line: found.line, action: "done" });
  if (!ok) {
    return;
  }
  leaveDecidedStory();
}

// Back to where the story was opened from, else to the Backlogs; the outcome notice stays for that one route.
function leaveDecidedStory() {
  keepEditNotice();
  if (openedFromList) {
    history.back();
    return;
  }
  history.replaceState(null, "", BacklogsRoute);
  route();
}

// A notice kept across the next route (route() calls routeEditNotice instead of hiding it).
let keptEditNotice = null;

function keepEditNotice() {
  const notice = document.getElementById("edit-notice");
  keptEditNotice = notice.hidden ? null : {
    text: document.getElementById("edit-notice-text").textContent,
    isError: notice.classList.contains("edit-notice-error"),
  };
}

function routeEditNotice() {
  const kept = keptEditNotice;
  keptEditNotice = null;
  if (kept !== null) {
    showEditNotice(kept.text, kept.isError);
  } else {
    hideEditNotice();
  }
}

document.getElementById("edit-notice-close").addEventListener("click", hideEditNotice);
