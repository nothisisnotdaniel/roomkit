"use strict";

// Health: the factory's problems from /api/doctor (stuck jobs, uncommitted job records, merged leftover branches,
// backlog story problems), read-only. Each section says in a fixed text what /factory doctor would do about it; the
// app fixes nothing. Fetched on every show and, while shown, when live.js sees changed jobs or backlogs (never on a
// timer of its own). Loaded after history.js and before live.js and app.js; their functions (element, asList,
// asText, asNumber, jobHash, storyHash, authFetch) are only called at run time. Every text is set with textContent.

const DoctorTexts = {
  loading: "Loading…",
  failed: "Could not load the health check.",
  healthy: "All healthy.",
  unreadable: "Could not check: ",
  stuck: "Stuck jobs",
  stuckHelp: "Doctor sets it to needs-human so you can approve or rework it.",
  uncommitted: "Uncommitted job records",
  uncommittedHelp: "Doctor commits and pushes the job folder.",
  branches: "Merged leftover branches",
  branchesHelp: "Doctor deletes the local branch with git branch -d.",
  stories: "Story problems",
  storiesHelp: "Doctor lists these only; fix the backlog by hand.",
  noUpdate: "no update time",
  hours: " h",
};

const DoctorProblems = {
  no_story: "No story link",
  duplicate: "Number used twice",
  missing: "Story file missing",
  bad_link: "Link is not a backlog story file",
  orphan: "No line links to this story file",
};

const DoctorRoute = "#doctor";
const DoctorApi = "/api/doctor";

let doctorRequest = 0;
// The last good answer, shown again at once on the next show while it is fetched again.
let doctorData = null;

function doctorShown() {
  return !document.getElementById("doctor").hidden;
}

function asTextList(value) {
  return asList(value).filter((item) => typeof item === "string" && item !== "");
}

// The response with only well-formed entries, or null when it has another shape.
function readDoctor(data) {
  if (!data || !Array.isArray(data.stuck) || !Array.isArray(data.uncommitted) || !Array.isArray(data.mergedBranches)
    || !Array.isArray(data.stories) || !Array.isArray(data.unreadable)) {
    return null;
  }
  const stuck = data.stuck
    .filter((item) => item && asText(item.id) !== "" && asText(item.stage) !== "")
    .map((item) => ({ id: item.id, project: asText(item.project), stage: item.stage, hours: asNumber(item.hours) }));
  const branches = data.mergedBranches
    .filter((item) => item && asText(item.project) !== "" && asText(item.branch) !== "")
    .map((item) => ({ project: item.project, branch: item.branch }));
  const stories = data.stories
    .filter((item) => item && asText(item.project) !== "" && typeof item.problem === "string"
      && Object.hasOwn(DoctorProblems, item.problem))
    .map((item) => ({
      project: item.project,
      number: asNumber(item.number),
      problem: item.problem,
      line: asText(item.line),
    }));
  return {
    stuck: stuck,
    uncommitted: asTextList(data.uncommitted),
    branches: branches,
    stories: stories,
    unreadable: asTextList(data.unreadable),
  };
}

// Builds the fixed parts once: a status line, the unreadable line and the sections.
function buildDoctorView() {
  if (document.getElementById("doctor-status")) {
    return;
  }
  const status = element("p", "status doctor-status", DoctorTexts.loading);
  status.id = "doctor-status";
  const unreadable = element("p", "error doctor-unreadable");
  unreadable.id = "doctor-unreadable";
  unreadable.hidden = true;
  const content = element("div", "doctor-content");
  content.id = "doctor-content";
  document.getElementById("doctor").replaceChildren(status, unreadable, content);
}

function showDoctorStatus(text, className) {
  const status = document.getElementById("doctor-status");
  status.className = className + " doctor-status";
  status.textContent = text;
  status.hidden = text === "";
}

// Fetches the report once and renders it while the view is shown. Returns false when it failed.
async function loadDoctor() {
  doctorRequest += 1;
  const request = doctorRequest;
  let data;
  try {
    const response = await authFetch(DoctorApi, { cache: "no-store" });
    if (!response.ok) {
      throw new Error("status");
    }
    data = readDoctor(await response.json());
    if (data === null) {
      throw new Error("data");
    }
  } catch {
    if (request === doctorRequest && doctorShown()) {
      showDoctorStatus(DoctorTexts.failed, "error");
      document.getElementById("doctor-unreadable").hidden = true;
      document.getElementById("doctor-content").replaceChildren();
    }
    return false;
  }
  if (request !== doctorRequest) {
    return true;
  }
  doctorData = data;
  if (doctorShown()) {
    const scroll = window.scrollY;
    renderDoctor();
    window.scrollTo(0, scroll);
  }
  return true;
}

function renderDoctor() {
  const data = doctorData;
  const unreadable = document.getElementById("doctor-unreadable");
  unreadable.textContent = data.unreadable.length > 0 ? DoctorTexts.unreadable + data.unreadable.join(", ") : "";
  unreadable.hidden = data.unreadable.length === 0;
  const sections = [];
  if (data.stuck.length > 0) {
    sections.push(renderDoctorSection("doctor-stuck", DoctorTexts.stuck, DoctorTexts.stuckHelp,
      data.stuck.map(renderDoctorStuck)));
  }
  if (data.uncommitted.length > 0) {
    sections.push(renderDoctorSection("doctor-uncommitted", DoctorTexts.uncommitted, DoctorTexts.uncommittedHelp,
      data.uncommitted.map(renderDoctorUncommitted)));
  }
  if (data.branches.length > 0) {
    sections.push(renderDoctorSection("doctor-branches", DoctorTexts.branches, DoctorTexts.branchesHelp,
      data.branches.map(renderDoctorBranch)));
  }
  if (data.stories.length > 0) {
    sections.push(renderDoctorSection("doctor-stories", DoctorTexts.stories, DoctorTexts.storiesHelp,
      data.stories.map(renderDoctorStory)));
  }
  document.getElementById("doctor-content").replaceChildren(...sections);
  if (sections.length === 0 && data.unreadable.length === 0) {
    showDoctorStatus(DoctorTexts.healthy, "empty");
  } else {
    showDoctorStatus("", "status");
  }
}

// One section: its heading, the fixed text of what doctor would do, then the items.
function renderDoctorSection(id, title, help, items) {
  const section = element("section", "doctor-section");
  section.setAttribute("aria-labelledby", id);
  const heading = element("h2", "doctor-heading", title);
  heading.id = id;
  const list = element("ul", "lines doctor-list");
  list.append(...items);
  section.append(heading, element("p", "doctor-help", help), list);
  return section;
}

function renderDoctorStuck(job) {
  const row = element("li", "doctor-item");
  if (job.project !== "") {
    row.appendChild(element("span", "doctor-project", job.project));
  }
  const link = element("a", "doctor-title", job.id);
  link.href = jobHash(job.id);
  row.appendChild(link);
  const age = job.hours === null ? DoctorTexts.noUpdate : job.hours + DoctorTexts.hours;
  row.appendChild(element("span", "doctor-facts", job.stage + ", " + age));
  return row;
}

function renderDoctorUncommitted(id) {
  const row = element("li", "doctor-item");
  const link = element("a", "doctor-title", id);
  link.href = jobHash(id);
  row.appendChild(link);
  return row;
}

function renderDoctorBranch(branch) {
  const row = element("li", "doctor-item");
  row.append(element("span", "doctor-project", branch.project), element("code", "doctor-branch", branch.branch));
  return row;
}

function renderDoctorStory(story) {
  const row = element("li", "doctor-item");
  row.appendChild(element("span", "doctor-project", story.project));
  if (story.number !== null) {
    const link = element("a", "doctor-title", "#" + story.number + " " + story.line);
    link.href = storyHash(story.project, story.number);
    row.appendChild(link);
  } else {
    row.appendChild(element("span", "doctor-line", story.line));
  }
  row.appendChild(element("span", "doctor-facts", DoctorProblems[story.problem]));
  return row;
}

// Called by app.js when the health view is shown (its heading is the header title): the last answer stays while
// it is fetched again, so nothing flashes.
function showDoctor() {
  buildDoctorView();
  if (doctorData !== null) {
    renderDoctor();
  }
  loadDoctor();
}
