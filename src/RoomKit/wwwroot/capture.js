"use strict";

// Capture, a tab with its own view again (#135): a quick idea from the phone becomes a numbered [?] draft in the
// chosen project's backlog, with a draft story, through action "capture" of /api/backlog/change. The draft waits for
// approval in the backlog (counted on the Backlogs tab) until it is rewritten and approved, so it never runs by
// itself. The project list comes from /api/backlogs on every show (readable projects only); the last used project is
// kept in localStorage. Loaded after search.js and before live.js and app.js; their functions (element, asList,
// asText, fetchBacklogs, sendChange) are only called at run time; sendChange refreshes the counts. Only fixed texts
// and project names through textContent are shown, never server error text.

const CaptureTexts = {
  projectLabel: "Project",
  ideaLabel: "Idea",
  hint: "Saved as a draft at the end of the backlog. Rewrite and approve it later.",
  save: "Save",
  loading: "Loading projects…",
  noProjects: "No backlog can be written to right now.",
  failed: "Can't load the projects right now.",
  saved: "Saved as a draft. It waits for approval in the backlog.",
};

const CaptureRoute = "#capture";
const CaptureMaxLength = 2000;

let captureRequest = 0;
let captureSaving = false;

function captureProject() {
  return document.getElementById("capture-project");
}

function captureText() {
  return document.getElementById("capture-text");
}

function captureSave() {
  return document.getElementById("capture-save");
}

function storedCaptureProject() {
  try {
    return localStorage.getItem(CaptureProjectKey) || "";
  } catch {
    return "";
  }
}

function storeCaptureProject(name) {
  try {
    localStorage.setItem(CaptureProjectKey, name);
  } catch {
    // Without storage the project is simply not preselected next time.
  }
}

function showCaptureStatus(text, className) {
  const status = document.getElementById("capture-status");
  status.className = (className || "status") + " capture-status";
  status.textContent = text;
  status.hidden = text === "";
}

// Save is off while the text is blank, no project is chosen or a change is being sent.
function updateCaptureSave() {
  const save = captureSave();
  if (save) {
    save.disabled = captureText().value.trim() === "" || captureProject().value === "" || changeBusy
      || captureSaving;
  }
}

// The form, built once on the first show; the text stays while the user goes elsewhere and comes back.
function buildCaptureView() {
  const main = document.getElementById("capture");
  const form = element("form", "capture-form");
  form.noValidate = true;

  const projectLabel = element("label", "capture-label", CaptureTexts.projectLabel);
  projectLabel.htmlFor = "capture-project";
  const project = element("select", "capture-select");
  project.id = "capture-project";
  project.addEventListener("change", () => {
    if (project.value !== "") {
      storeCaptureProject(project.value);
    }
    updateCaptureSave();
  });

  const ideaLabel = element("label", "capture-label", CaptureTexts.ideaLabel);
  ideaLabel.htmlFor = "capture-text";
  const area = element("textarea", "capture-text");
  area.id = "capture-text";
  area.rows = 6;
  area.maxLength = CaptureMaxLength;
  area.setAttribute("maxlength", "2000");
  area.spellcheck = true;
  area.setAttribute("autocapitalize", "sentences");
  const hint = element("p", "capture-hint", CaptureTexts.hint);
  hint.id = "capture-hint";
  area.setAttribute("aria-describedby", hint.id);
  area.addEventListener("input", () => {
    showCaptureStatus("");
    updateCaptureSave();
  });

  const save = element("button", "capture-save", CaptureTexts.save);
  save.id = "capture-save";
  save.type = "submit";
  save.disabled = true;

  const status = element("p", "status capture-status");
  status.id = "capture-status";
  status.setAttribute("role", "status");
  status.setAttribute("aria-live", "polite");
  status.hidden = true;

  form.append(projectLabel, project, ideaLabel, area, hint, save);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    saveCapture();
  });
  main.replaceChildren(form, status);
}

// Fills the project list with the readable projects; the stored one (or the one chosen before) is selected while it
// is still there.
function fillCaptureProjects(names) {
  const select = captureProject();
  const wanted = select.value !== "" ? select.value : storedCaptureProject();
  const options = names.map((name) => {
    const option = element("option", null, name);
    option.value = name;
    return option;
  });
  select.replaceChildren(...options);
  if (names.includes(wanted)) {
    select.value = wanted;
  }
}

// The names of the projects whose backlog can be read (version set), from the /api/backlogs answer.
function captureProjectNames(data) {
  return asList(data && data.projects)
    .filter((project) => project && asText(project.name) !== "" && asText(project.version) !== "")
    .map((project) => project.name);
}

async function showCapture() {
  if (!captureProject()) {
    buildCaptureView();
  }
  captureRequest += 1;
  const request = captureRequest;
  if (captureProject().options.length === 0) {
    showCaptureStatus(CaptureTexts.loading);
  }
  let data;
  try {
    data = await fetchBacklogs();
  } catch {
    if (request === captureRequest && !tokenViewShown()) {
      showCaptureStatus(CaptureTexts.failed, "error");
    }
    return;
  }
  if (request !== captureRequest) {
    return;
  }
  const names = captureProjectNames(data);
  fillCaptureProjects(names);
  showCaptureStatus(names.length === 0 ? CaptureTexts.noProjects : "", names.length === 0 ? "empty" : "status");
  updateCaptureSave();
}

// Save: the project's current version from /api/backlogs, then action "capture" through sendChange. On success the
// text is cleared; on failure it stays and sendChange shows the fixed ChangeErrors text.
async function saveCapture() {
  const project = captureProject().value;
  const area = captureText();
  if (changeBusy || captureSaving || project === "" || area.value.trim() === "") {
    return;
  }
  captureSaving = true;
  updateCaptureSave();
  showCaptureStatus("");
  let version = "";
  try {
    const data = await fetchBacklogs();
    const found = asList(data && data.projects).find((item) => item && item.name === project);
    version = found ? asText(found.version) : "";
  } catch {
    captureSaving = false;
    updateCaptureSave();
    if (!tokenViewShown()) {
      showEditNotice(ChangeErrors.unreachable, true);
    }
    return;
  }
  if (version === "") {
    captureSaving = false;
    updateCaptureSave();
    showEditNotice(ChangeErrors.invalid, true);
    return;
  }
  const ok = await sendChange({
    project: project,
    version: version,
    action: "capture",
    text: area.value,
  });
  captureSaving = false;
  if (ok) {
    storeCaptureProject(project);
    area.value = "";
    showCaptureStatus(CaptureTexts.saved, "capture-saved");
  }
  updateCaptureSave();
}
