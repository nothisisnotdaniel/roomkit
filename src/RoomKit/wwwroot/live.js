"use strict";

// Live updates: one timer asks /api/changes (metadata fingerprints) every PollInterval while the page is visible,
// and only what changed is fetched again: the board (pollBoard in board.js), the backlog sections of changed
// projects, the open story (app.js), the counts on the Backlogs and Jobs tabs (counts.js), the current search (search.js),
// the history (history.js), the health view (doctor.js), the project status lines (status.js) and the release blocks
// (releases.js) while shown. A failed check shows one fixed connection notice. Loaded after releases.js and before
// app.js; the functions of the other scripts are only called at run time. Fingerprints are compared, never shown;
// only fixed texts are rendered, with textContent.

const LiveTexts = {
  lost: "Connection lost. Reconnecting…",
};

const ChangesRoute = "/api/changes";
// A check that gets no answer in this time counts as failed, so a dropped connection can't stop the checks.
const CheckTimeout = 10000;

let liveTimer = null;
let livePending = false;
let liveLost = false;
let liveLast = null;
// What still has to be fetched again: the board, backlog sections (project names), the open story's project and,
// after a lost connection, the open job page.
let boardStale = false;
let jobStale = false;
// Any project's backlog changed: the [h] and [?] counts on the Backlogs tab are fetched again.
let countsStale = false;
// The jobs changed: the running and blocked counts on the Jobs tab (counts.js) are fetched again, on every view.
let jobCountsStale = false;
// The jobs changed: the history (history.js) is fetched again while it is shown; showing it fetches it anyway.
let historyStale = false;
// The jobs, a backlog or the story files changed: the health view (doctor.js) is fetched again while it is shown.
let doctorStale = false;
// The jobs or a backlog changed: the status lines on the Backlogs view (status.js) are fetched again while it is shown.
let statusStale = false;
// The jobs or a backlog changed: the release blocks on the Backlogs view (releases.js) are fetched again while it is
// shown.
let releasesStale = false;
// The same for the current search (search.js), re-run while shown and on the next show while hidden.
let searchChanged = false;
const staleBacklogs = new Set();
const staleStories = new Set();
let backlogsRefreshing = false;

function connectionLost() {
  return liveLost;
}

// The notice under the header; on the board the board-status line shows the same text instead.
function showConnectionState() {
  const notice = document.getElementById("live-notice");
  const onBoard = !document.getElementById("board").hidden;
  const shown = liveLost && !onBoard;
  if (shown !== !notice.hidden) {
    notice.textContent = shown ? LiveTexts.lost : "";
    notice.hidden = !shown;
  }
  if (liveLost) {
    const status = document.getElementById("board-status");
    status.className = "live-status error";
    status.textContent = LiveTexts.lost;
  }
}

function setConnectionLost(lost) {
  const was = liveLost;
  liveLost = lost;
  showConnectionState();
  if (was && !lost) {
    setBoardStatus(true);
  }
}

// The response as { jobs, projects: Map(name -> { backlog, stories, claims }) }, or null when it has another shape.
// claims (other machines' item claims) is "" when the server sends none.
function readChanges(data) {
  if (!data || typeof data.jobs !== "string" || !data.projects || typeof data.projects !== "object"
    || Array.isArray(data.projects)) {
    return null;
  }
  const projects = new Map();
  for (const [name, value] of Object.entries(data.projects)) {
    if (!value || typeof value.backlog !== "string" || typeof value.stories !== "string"
      || (value.claims !== undefined && typeof value.claims !== "string")) {
      return null;
    }
    projects.set(name, { backlog: value.backlog, stories: value.stories, claims: value.claims || "" });
  }
  return { jobs: data.jobs, projects: projects };
}

// True when the jobs, the project list or any project's backlog or story files changed since the previous check (never
// for the first check, the baseline).
function doctorInputsChanged(previous, next) {
  if (previous === null) {
    return false;
  }
  if (previous.jobs !== next.jobs || previous.projects.size !== next.projects.size) {
    return true;
  }
  for (const [name, after] of next.projects) {
    const before = previous.projects.get(name);
    if (!before || before.backlog !== after.backlog || before.stories !== after.stories) {
      return true;
    }
  }
  return false;
}

// True when the jobs, the project list or any project's backlog changed since the previous check (never for the
// first check, the baseline).
function statusInputsChanged(previous, next) {
  if (previous === null) {
    return false;
  }
  if (previous.jobs !== next.jobs || previous.projects.size !== next.projects.size) {
    return true;
  }
  for (const [name, after] of next.projects) {
    const before = previous.projects.get(name);
    if (!before || before.backlog !== after.backlog) {
      return true;
    }
  }
  return false;
}

// Marks what changed since the last check. The first check is the baseline; after a lost connection everything
// shown is fetched once.
function noteChanges(next, recovered) {
  const previous = liveLast;
  liveLast = next;
  // The history follows the jobs fingerprint only; after a lost connection it is fetched once as well.
  if (recovered || (previous !== null && previous.jobs !== next.jobs)) {
    historyStale = true;
  }
  // The health view follows the jobs, every backlog and the story files; after a lost connection it is fetched once.
  if (recovered || doctorInputsChanged(previous, next)) {
    doctorStale = true;
  }
  // The status lines follow the jobs and every backlog (not the story files); after a lost connection they are
  // fetched once.
  if (recovered || statusInputsChanged(previous, next)) {
    statusStale = true;
    releasesStale = true;
  }
  const names = new Set([...next.projects.keys(), ...(previous ? previous.projects.keys() : [])]);
  if (recovered) {
    boardStale = true;
    jobStale = true;
    countsStale = true;
    jobCountsStale = true;
    for (const name of names) {
      staleBacklogs.add(name);
      staleStories.add(name);
    }
    searchChanged = true;
    return;
  }
  if (previous === null) {
    return;
  }
  if (previous.jobs !== next.jobs) {
    boardStale = true;
    jobCountsStale = true;
  }
  for (const name of names) {
    const before = previous.projects.get(name);
    const after = next.projects.get(name);
    const backlogChanged = !before || !after || before.backlog !== after.backlog;
    if (backlogChanged) {
      boardStale = true;
      countsStale = true;
      staleBacklogs.add(name);
      staleStories.add(name);
      searchChanged = true;
    } else if (before.stories !== after.stories) {
      staleStories.add(name);
      searchChanged = true;
    }
    // Another machine's claims changed: the section is fetched again, like a changed backlog.
    if (!backlogChanged && before.claims !== after.claims) {
      staleBacklogs.add(name);
    }
  }
}

// While a drag is in progress (dragBusy in edit.js) no section is replaced; the stale names wait for the next check.
async function refreshStaleBacklogs() {
  if (dragBusy || backlogsRefreshing || staleBacklogs.size === 0 || document.getElementById("backlogs").hidden) {
    return;
  }
  backlogsRefreshing = true;
  const names = [...staleBacklogs];
  staleBacklogs.clear();
  try {
    for (const name of await refreshBacklogs(names)) {
      staleBacklogs.add(name);
    }
  } finally {
    backlogsRefreshing = false;
  }
}

async function refreshStaleStory() {
  const project = openStoryProject();
  const wanted = project !== null && staleStories.has(project);
  staleStories.clear();
  if (wanted && !(await refreshStory())) {
    staleStories.add(project);
  }
}

async function refreshShownViews() {
  if (boardViewsVisible()) {
    // A board fetch that failed earlier (the line shows an error) is tried again too.
    if (boardStale || document.getElementById("board-status").classList.contains("error")) {
      if (await pollBoard()) {
        boardStale = false;
        // pollBoard has just set the Jobs tab counts from the same answer, so they need no fetch of their own.
        jobCountsStale = false;
      }
    } else {
      setBoardStatus(true);
    }
    if (jobStale && openJobId !== null && !document.getElementById("job").hidden) {
      loadJob(openJobId, true);
    }
    // Running times on the cards and the job page follow the clock without a refetch (board.js).
    updateRunningTimes();
  } else {
    setBoardStatus(true);
  }
  jobStale = false;
  await refreshStaleBacklogs();
  await refreshStaleStory();
  await refreshStaleSearch();
  await refreshStaleHistory();
  await refreshStaleDoctor();
  await refreshStaleStatus();
  await refreshStaleReleases();
  await refreshStaleCounts();
  await refreshStaleJobCounts();
}

// The Jobs tab counts follow the jobs fingerprint on every view; a failed fetch is tried again on the next check.
async function refreshStaleJobCounts() {
  if (!jobCountsStale) {
    return;
  }
  jobCountsStale = false;
  if (!(await loadJobCounts())) {
    jobCountsStale = true;
  }
}

// The counts follow every backlog change; a failed fetch is tried again on the next check.
async function refreshStaleCounts() {
  if (!countsStale) {
    return;
  }
  countsStale = false;
  if (!(await loadBacklogCounts())) {
    countsStale = true;
  }
}

// A changed jobs fingerprint fetches the history again while it is shown; while hidden there is nothing to do, as
// showing it fetches it. A failed fetch is tried again on the next check.
async function refreshStaleHistory() {
  if (!historyStale) {
    return;
  }
  historyStale = false;
  if (document.getElementById("history").hidden) {
    return;
  }
  if (!(await loadHistory())) {
    historyStale = true;
  }
}

// A changed jobs, backlog or story fingerprint fetches the health view again while it is shown; while hidden there is
// nothing to do, as showing it fetches it. A failed fetch is tried again on the next check.
async function refreshStaleDoctor() {
  if (!doctorStale) {
    return;
  }
  doctorStale = false;
  if (document.getElementById("doctor").hidden) {
    return;
  }
  if (!(await loadDoctor())) {
    doctorStale = true;
  }
}

// A changed jobs or backlog fingerprint fetches the status lines again while the Backlogs view is shown; while hidden
// there is nothing to do, as showing it fetches them. A failed fetch, or a line left alone because it has focus, is
// tried again on the next check.
async function refreshStaleStatus() {
  if (!statusStale) {
    return;
  }
  statusStale = false;
  if (document.getElementById("backlogs").hidden) {
    return;
  }
  if (!(await loadStatus())) {
    statusStale = true;
  }
}

// A changed jobs or backlog fingerprint fetches the release blocks again while the Backlogs view is shown; while
// hidden there is nothing to do, as showing it fetches them. A failed fetch, or a block left alone because it has
// focus, is tried again on the next check.
async function refreshStaleReleases() {
  if (!releasesStale) {
    return;
  }
  releasesStale = false;
  if (document.getElementById("backlogs").hidden) {
    return;
  }
  if (!(await loadReleases())) {
    releasesStale = true;
  }
}

// A changed backlog or story fingerprint re-runs the current search (only its results are replaced); while Search is
// hidden, refreshSearch marks it so the next show re-runs it. A failed re-run is tried again on the next check.
async function refreshStaleSearch() {
  if (!searchChanged) {
    return;
  }
  searchChanged = false;
  if (!(await refreshSearch())) {
    searchChanged = true;
  }
}

// One check: only while visible, with no token view and no earlier check pending. A 401 shows the token view
// (authFetch) and stops checks until a token is saved.
async function checkChanges() {
  if (document.visibilityState !== "visible" || tokenViewShown() || livePending) {
    return;
  }
  livePending = true;
  try {
    let next;
    try {
      const response = await authFetch(ChangesRoute, { cache: "no-store", signal: AbortSignal.timeout(CheckTimeout) });
      if (!response.ok) {
        throw new Error("status");
      }
      next = readChanges(await response.json());
      if (next === null) {
        throw new Error("data");
      }
    } catch {
      if (!tokenViewShown()) {
        setConnectionLost(true);
      }
      return;
    }
    const recovered = liveLost;
    setConnectionLost(false);
    noteChanges(next, recovered);
    await refreshShownViews();
  } finally {
    livePending = false;
  }
}

// Called by app.js after every route change: the notice follows the view, and a list shown again gets the
// sections that changed while it was hidden.
function liveRouteChanged() {
  showConnectionState();
  staleStories.clear();
  refreshStaleBacklogs();
}

// Called once by app.js: the one timer, a check at once, and a check when the page becomes visible again.
function startLiveChecks() {
  if (liveTimer !== null) {
    return;
  }
  liveTimer = setInterval(checkChanges, PollInterval);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") {
      checkChanges();
    }
  });
  checkChanges();
}
