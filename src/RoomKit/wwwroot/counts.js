"use strict";

// The two counts on the Backlogs tab icon (#135): the user's [h] tasks and the unapproved [?] items across every
// readable project, from /api/backlogs (the same answer the Backlogs view shows). Each badge is hidden at 0 or while
// the count is unknown (not loaded yet, or the last load failed); a shown one has a visually hidden text, so the link
// reads for example "Backlogs, 2 tasks, 5 unapproved". Loaded after edit.js and before live.js and app.js; their
// functions (element, asList, fetchBacklogs) are only called at run time. Only numbers and fixed texts are shown,
// through textContent.
//
// The two counts on the Jobs tab icon (#138): running jobs and jobs blocked on the user (needs-human) across every
// project, from /api/board (the same jobs the board shows, ignoring its project filter), so the link reads for example
// "Jobs, 1 running, 2 blocked". fetchBoard and RunningStages come from board.js; pollBoard sets these counts from
// every board answer it reads.

// Host hooks (RoomKit #5): a host app's script, loaded after this one, registers its own code with
// roomKitHooks.on(name, fn) for one of HostHookNames: "jobCounts" runs after loadJobCounts has set new counts (given
// the board answer's jobs), "liveChecked" after each live check while the board views are shown (live.js, given
// { jobsChanged, recovered, firstCheck }). A name's hooks run in registration order, each awaited; one that throws
// or rejects is written to console.error and the next one runs. The registry is frozen and its list is closed over,
// so no script can replace or reach it; runRoomKitHooks is only for counts.js and live.js.
const HostHookNames = Object.freeze(["jobCounts", "liveChecked"]);

const [roomKitHooks, runRoomKitHooks] = (() => {
  const hooks = new Map(HostHookNames.map((name) => [name, []]));

  function on(name, fn) {
    if (typeof name !== "string" || !hooks.has(name)) {
      throw new TypeError("roomKitHooks.on: unknown hook name");
    }
    if (typeof fn !== "function") {
      throw new TypeError("roomKitHooks.on: the hook is not a function");
    }
    hooks.get(name).push(fn);
  }

  async function run(name, argument) {
    for (const fn of [...hooks.get(name)]) {
      try {
        await fn(argument);
      } catch (error) {
        console.error("roomKitHooks: a " + name + " hook failed", error);
      }
    }
  }

  return [Object.freeze({ on: on }), run];
})();

const CountTexts = {
  task: " task",
  tasks: " tasks",
  unapproved: " unapproved",
  running: " running",
  blocked: " blocked",
};

// null while unknown: both badges are hidden then. A load in progress keeps the last values, so nothing flashes 0.
let backlogCounts = null;
let backlogCountsRequest = 0;
let jobCounts = null;
let jobCountsRequest = 0;

// One badge: the number for the eye (aria-hidden) and ", N <noun>" for screen readers; hidden at 0 or unknown.
function showCountBadge(id, count, noun) {
  const badge = document.getElementById(id);
  const shown = count !== null && count > 0;
  if (!shown) {
    badge.replaceChildren();
    badge.hidden = true;
    return;
  }
  const number = element("span", null, String(count));
  number.setAttribute("aria-hidden", "true");
  badge.replaceChildren(number, element("span", "visually-hidden", ", " + count + noun));
  badge.hidden = false;
}

function showBacklogCounts() {
  const tasks = backlogCounts === null ? null : backlogCounts.tasks;
  const approval = backlogCounts === null ? null : backlogCounts.approval;
  showCountBadge("tasks-count", tasks, tasks === 1 ? CountTexts.task : CountTexts.tasks);
  showCountBadge("approval-count", approval, CountTexts.unapproved);
}

// The [h] (user) and [?] (approval) entries of every readable project, or null when the answer has another shape.
function countBacklogs(data) {
  if (!data || !Array.isArray(data.projects)) {
    return null;
  }
  let tasks = 0;
  let approval = 0;
  for (const project of data.projects) {
    if (!project || project.readable !== true) {
      continue;
    }
    tasks += asList(project.user).length;
    approval += asList(project.approval).length;
  }
  return { tasks: tasks, approval: approval };
}

// Fetches the counts once; an older answer than the last request is dropped. Returns false when it failed, so the
// live check tries again.
async function loadBacklogCounts() {
  backlogCountsRequest += 1;
  const request = backlogCountsRequest;
  let counts;
  try {
    counts = countBacklogs(await fetchBacklogs());
    if (counts === null) {
      throw new Error("data");
    }
  } catch {
    if (request === backlogCountsRequest) {
      backlogCounts = null;
      showBacklogCounts();
    }
    return false;
  }
  if (request === backlogCountsRequest) {
    backlogCounts = counts;
    showBacklogCounts();
  }
  return true;
}

function showJobCounts() {
  showCountBadge("running-count", jobCounts === null ? null : jobCounts.running, CountTexts.running);
  showCountBadge("blocked-count", jobCounts === null ? null : jobCounts.blocked, CountTexts.blocked);
}

// Running jobs (a stage in RunningStages) and blocked ones (needs-human) over every job of the board answer; done and
// unknown stages count in neither. null when the answer has no jobs list.
function countJobs(jobs) {
  if (!Array.isArray(jobs)) {
    return null;
  }
  let running = 0;
  let blocked = 0;
  for (const job of jobs) {
    if (!job || typeof job !== "object") {
      continue;
    }
    if (RunningStages.includes(job.stage)) {
      running += 1;
    } else if (job.stage === "needs-human") {
      blocked += 1;
    }
  }
  return { running: running, blocked: blocked };
}

// Sets the job counts from a board answer's jobs (pollBoard), or makes them unknown with null. Any load still in
// flight is dropped, as this answer is at least as new.
function setJobCounts(counts) {
  jobCountsRequest += 1;
  jobCounts = counts;
  showJobCounts();
}

// Fetches the board once for the counts; an older answer than the last request is dropped. Returns false when it
// failed, so the live check tries again. After new counts are set, the "jobCounts" host hooks get the answer's jobs.
async function loadJobCounts() {
  jobCountsRequest += 1;
  const request = jobCountsRequest;
  let counts;
  let jobs;
  try {
    const data = JSON.parse(await fetchBoard());
    jobs = data && data.jobs;
    counts = countJobs(jobs);
    if (counts === null) {
      throw new Error("data");
    }
  } catch {
    if (request === jobCountsRequest) {
      jobCounts = null;
      showJobCounts();
    }
    return false;
  }
  if (request === jobCountsRequest) {
    jobCounts = counts;
    showJobCounts();
    await runRoomKitHooks("jobCounts", jobs);
  }
  return true;
}
