"use strict";

// Runs one HostHooksScriptTests scenario: loads the real counts.js and live.js into a node:vm context with stub
// globals for everything else they touch, runs the named scenario and prints its result as one JSON line. Usage:
// node HostHooksHarness.js <scenario.json>, where the file holds { "name": "...", "wwwroot": "<folder>" }.
// No timer, no clock and no network: fetchBoard and authFetch return canned answers or promises the scenario settles
// itself, and every await is on a returned promise (or one turn of the event loop), never a delay.

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

function fakeElement(id) {
  const item = {
    id: id,
    hidden: id !== "board",
    textContent: "",
    className: "",
    children: [],
    attributes: {},
    replaceChildren(...children) {
      item.children = children;
    },
    setAttribute(name, value) {
      item.attributes[name] = value;
    },
  };
  item.classList = {
    contains(name) {
      return item.className.split(" ").includes(name);
    },
  };
  return item;
}

function makeRoom() {
  const log = [];
  const elements = new Map();
  const state = {
    boardVisible: true,
    boardAnswers: [],
    changesAnswers: [],
    tokenView: false,
  };
  const documentStub = {
    visibilityState: "visible",
    getElementById(id) {
      if (!elements.has(id)) {
        elements.set(id, fakeElement(id));
      }
      return elements.get(id);
    },
    addEventListener() {
      throw new Error("startLiveChecks must not run");
    },
  };
  // The next canned answer: a string, an Error (rejects) or a deferred (settled by the scenario).
  function next(queue, what) {
    if (queue.length === 0) {
      throw new Error("no canned " + what);
    }
    const answer = queue.shift();
    if (answer instanceof Error) {
      return Promise.reject(answer);
    }
    if (answer && answer.promise) {
      return answer.promise;
    }
    return Promise.resolve(answer);
  }
  const sandbox = {
    console: {
      error() {
        log.push("console.error");
      },
      log() {},
    },
    document: documentStub,
    AbortSignal: { timeout: () => "signal" },
    PollInterval: 1000,
    RunningStages: ["build", "review"],
    openJobId: null,
    dragBusy: false,
    element: (tag, className, text) => {
      const item = fakeElement(null);
      item.tag = tag;
      item.className = className || "";
      item.textContent = text || "";
      return item;
    },
    asList: (value) => (Array.isArray(value) ? value : []),
    fetchBacklogs: () => Promise.resolve({ projects: [] }),
    fetchBoard: () => {
      log.push("fetchBoard");
      return next(state.boardAnswers, "board answer");
    },
    authFetch: () => {
      log.push("authFetch");
      return next(state.changesAnswers, "changes answer");
    },
    tokenViewShown: () => state.tokenView,
    boardViewsVisible: () => state.boardVisible,
    pollBoard: async () => {
      log.push("pollBoard");
      return true;
    },
    setBoardStatus: () => log.push("setBoardStatus"),
    loadJob: () => log.push("loadJob"),
    updateRunningTimes: () => log.push("updateRunningTimes"),
    refreshBacklogs: async () => {
      log.push("refreshBacklogs");
      return [];
    },
    openStoryProject: () => null,
    refreshStory: async () => true,
    refreshSearch: async () => {
      log.push("refreshSearch");
      return true;
    },
    loadHistory: async () => true,
    loadDoctor: async () => true,
    loadStatus: async () => true,
    loadReleases: async () => true,
  };
  const context = vm.createContext(sandbox);
  for (const file of ["counts.js", "live.js"]) {
    const code = fs.readFileSync(path.join(scenario.wwwroot, file), "utf8");
    vm.runInContext(code, context, { filename: file });
  }
  const run = (code) => vm.runInContext(code, context);
  // Wrap the steps under test so the log shows their order; the originals still run.
  run(`
    const harnessNoteChanges = noteChanges;
    noteChanges = function (next, recovered) {
      harnessLog("noteChanges");
      return harnessNoteChanges(next, recovered);
    };
    const harnessRefreshShownViews = refreshShownViews;
    refreshShownViews = function () {
      harnessLog("refreshShownViews");
      return harnessRefreshShownViews();
    };
    const harnessParse = JSON.parse;
    JSON.parse = function (text) {
      const value = harnessParse(text);
      globalThis.harnessLastParsed = value;
      return value;
    };
  `);
  sandbox.harnessLog = (entry) => log.push(entry);
  return { log: log, state: state, sandbox: sandbox, document: documentStub, run: run };
}

function changes(jobs) {
  return { ok: true, json: async () => ({ jobs: jobs, projects: { Demo: { backlog: "b", stories: "s" } } }) };
}

function board(jobs) {
  return JSON.stringify({ jobs: jobs });
}

const SampleJobs = [{ stage: "build" }, { stage: "needs-human" }, { stage: "done" }, { stage: "review" }];

function isTypeError(error) {
  return error !== null && typeof error === "object" && error.name === "TypeError";
}

// A hook that logs its name, the counts and the jobs it got.
function jobCountsRecorder(room, label, seen) {
  return (jobs) => {
    const counts = room.run("jobCounts");
    room.log.push("jobCounts:" + label + ":" + (counts === null ? "null" : counts.running + "/" + counts.blocked));
    if (seen) {
      seen.push(jobs);
    }
  };
}

// A hook that logs its name, its payload and what liveLast holds when it runs.
function liveRecorder(room, label) {
  return (info) => {
    const last = room.run("liveLast");
    room.log.push("liveChecked:" + label + ":first=" + info.firstCheck + ",jobs=" + info.jobsChanged
      + ",recovered=" + info.recovered + ",last=" + (last === null ? "null" : last.jobs)
      + ",keys=" + Object.keys(info).sort().join("+"));
  };
}

const scenarios = {
  async registry() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    const errors = {};
    const attempts = {
      unknownName: () => hooks.on("other", () => room.log.push("bad")),
      emptyName: () => hooks.on("", () => room.log.push("bad")),
      protoName: () => hooks.on("__proto__", () => room.log.push("bad")),
      constructorName: () => hooks.on("constructor", () => room.log.push("bad")),
      caseName: () => hooks.on("JobCounts", () => room.log.push("bad")),
      noName: () => hooks.on(undefined, () => room.log.push("bad")),
      nullHook: () => hooks.on("jobCounts", null),
      stringHook: () => hooks.on("liveChecked", "alert(1)"),
      objectHook: () => hooks.on("jobCounts", {}),
      missingHook: () => hooks.on("liveChecked"),
    };
    for (const [name, attempt] of Object.entries(attempts)) {
      try {
        attempt();
        errors[name] = "none";
      } catch (error) {
        errors[name] = isTypeError(error) ? "TypeError" : "other";
      }
    }
    const results = {};
    results.validJobCounts = hooks.on("jobCounts", () => room.log.push("jobCounts:valid"));
    results.validLiveChecked = hooks.on("liveChecked", () => room.log.push("liveChecked:valid"));
    const on = hooks.on;
    // A strict script throws on the frozen object; a sloppy one has its assignment ignored.
    let assignOn = "none";
    try {
      room.run("\"use strict\"; roomKitHooks.on = function () {};");
    } catch (error) {
      assignOn = isTypeError(error) ? "TypeError" : "other";
    }
    let sloppyAssign;
    try {
      room.run("roomKitHooks.on = function () {}; roomKitHooks.extra = 1;");
      sloppyAssign = "ignored";
    } catch (error) {
      sloppyAssign = isTypeError(error) ? "TypeError" : "other";
    }
    let reassign = "none";
    try {
      room.run("roomKitHooks = {};");
    } catch (error) {
      reassign = isTypeError(error) ? "TypeError" : "other";
    }
    room.state.boardAnswers.push(board(SampleJobs));
    room.state.changesAnswers.push(changes("j1"));
    const loaded = await room.sandbox.loadJobCounts();
    await room.sandbox.checkChanges();
    return {
      errors: errors,
      validReturns: [results.validJobCounts === undefined, results.validLiveChecked === undefined],
      frozen: Object.isFrozen(hooks),
      keys: Object.keys(hooks),
      sameOn: room.run("roomKitHooks.on") === on,
      extra: room.run("roomKitHooks.extra === undefined"),
      onGlobalObject: Object.prototype.hasOwnProperty.call(room.sandbox, "roomKitHooks"),
      assignOn: assignOn,
      sloppyAssign: sloppyAssign,
      reassign: reassign,
      loaded: loaded,
      log: room.log,
    };
  },

  async jobCountsSuccess() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    const seen = [];
    hooks.on("jobCounts", jobCountsRecorder(room, "a", seen));
    hooks.on("jobCounts", async (jobs) => {
      await drain();
      jobCountsRecorder(room, "b", seen)(jobs);
    });
    hooks.on("jobCounts", jobCountsRecorder(room, "c", seen));
    room.state.boardAnswers.push(board(SampleJobs));
    const loaded = await room.sandbox.loadJobCounts();
    const parsed = room.run("harnessLastParsed");
    return {
      loaded: loaded,
      counts: room.run("jobCounts"),
      sameArray: seen.length === 3 && seen.every((jobs) => jobs === parsed.jobs),
      jobs: seen[0],
      log: room.log,
    };
  },

  async jobCountsFailures() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    const seen = [];
    hooks.on("jobCounts", jobCountsRecorder(room, "a", seen));
    const results = {};
    room.state.boardAnswers.push(new Error("offline"));
    results.failedFetch = await room.sandbox.loadJobCounts();
    room.state.boardAnswers.push("{}");
    results.noJobs = await room.sandbox.loadJobCounts();
    room.state.boardAnswers.push(JSON.stringify({ jobs: "many" }));
    results.notArray = await room.sandbox.loadJobCounts();
    room.state.boardAnswers.push("not json");
    results.notJson = await room.sandbox.loadJobCounts();
    results.countsAfterFailures = room.run("jobCounts");
    room.log.push("-- overlap");
    const first = deferred();
    room.state.boardAnswers.push(first);
    room.state.boardAnswers.push(board([{ stage: "build" }]));
    const older = room.sandbox.loadJobCounts();
    const newer = room.sandbox.loadJobCounts();
    results.newer = await newer;
    first.resolve(board(SampleJobs));
    results.older = await older;
    results.counts = room.run("jobCounts");
    results.seenLengths = seen.map((jobs) => jobs.length);
    results.log = room.log;
    return results;
  },

  async liveSequence() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    hooks.on("liveChecked", liveRecorder(room, "a"));
    hooks.on("liveChecked", async (info) => {
      await drain();
      liveRecorder(room, "b")(info);
    });
    const steps = [new Error("offline"), changes("j1"), changes("j1"), changes("j2"), new Error("offline"),
      changes("j2"), changes("j3")];
    const logs = [];
    for (const step of steps) {
      room.state.changesAnswers.push(step);
      room.log.length = 0;
      await room.sandbox.checkChanges();
      logs.push([...room.log]);
    }
    return { logs: logs, pending: room.run("livePending") };
  },

  async livePendingHook() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    const entered = deferred();
    const release = deferred();
    hooks.on("liveChecked", (info) => {
      room.log.push("liveChecked:held");
      entered.resolve();
      return release.promise;
    });
    hooks.on("liveChecked", () => room.log.push("liveChecked:after"));
    room.state.changesAnswers.push(changes("j1"));
    const check = room.sandbox.checkChanges();
    await entered.promise;
    // A few event-loop turns let any refresh that wrongly ran past the held hook show up in the log. This can only
    // miss a late refresh, never fail at random: no refresh may run until release resolves.
    await drain();
    await drain();
    const whileHeld = [...room.log];
    const pendingWhileHeld = room.run("livePending");
    release.resolve();
    await check;
    return { whileHeld: whileHeld, pendingWhileHeld: pendingWhileHeld, log: room.log, pending: room.run("livePending") };
  },

  async liveSkipped() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    hooks.on("liveChecked", liveRecorder(room, "a"));
    const logs = {};
    async function check(name, prepare, answer) {
      room.log.length = 0;
      prepare();
      if (answer !== undefined) {
        room.state.changesAnswers.push(answer);
      }
      await room.sandbox.checkChanges();
      logs[name] = [...room.log];
    }
    const reset = () => {
      room.state.boardVisible = true;
      room.state.tokenView = false;
      room.document.visibilityState = "visible";
    };
    await check("boardHidden", () => {
      reset();
      room.state.boardVisible = false;
    }, changes("j1"));
    await check("boardHiddenChanged", () => {
      reset();
      room.state.boardVisible = false;
    }, changes("j2"));
    await check("failed", reset, new Error("offline"));
    await check("notOk", reset, { ok: false, json: async () => ({}) });
    await check("badData", reset, { ok: true, json: async () => ({ jobs: 1 }) });
    room.state.changesAnswers.length = 0;
    await check("pageHidden", () => {
      reset();
      room.document.visibilityState = "hidden";
    });
    await check("tokenView", () => {
      reset();
      room.state.tokenView = true;
    });
    // A check still waiting for its answer: a second one returns at once.
    reset();
    room.log.length = 0;
    const held = deferred();
    room.state.changesAnswers.push(held);
    const first = room.sandbox.checkChanges();
    await room.sandbox.checkChanges();
    logs.checkPending = [...room.log];
    room.state.boardVisible = false;
    held.resolve(changes("j3"));
    await first;
    return { logs: logs, pending: room.run("livePending") };
  },

  async errors() {
    const room = makeRoom();
    const hooks = room.run("roomKitHooks");
    hooks.on("jobCounts", () => {
      room.log.push("jobCounts:throws");
      throw new Error("hook");
    });
    hooks.on("jobCounts", async () => {
      room.log.push("jobCounts:rejects");
      throw new Error("hook");
    });
    hooks.on("jobCounts", jobCountsRecorder(room, "after"));
    hooks.on("liveChecked", () => {
      room.log.push("liveChecked:throws");
      throw new Error("hook");
    });
    hooks.on("liveChecked", () => {
      room.log.push("liveChecked:rejects");
      return Promise.reject(new Error("hook"));
    });
    hooks.on("liveChecked", liveRecorder(room, "after"));
    room.state.boardAnswers.push(board(SampleJobs));
    const loaded = await room.sandbox.loadJobCounts();
    const counts = room.run("jobCounts");
    const countsLog = [...room.log];
    const checks = [];
    for (const jobs of ["j1", "j2"]) {
      room.log.length = 0;
      room.state.changesAnswers.push(changes(jobs));
      await room.sandbox.checkChanges();
      checks.push({ log: [...room.log], pending: room.run("livePending") });
    }
    return { loaded: loaded, counts: counts, countsLog: countsLog, checks: checks };
  },

  async noHooks() {
    const room = makeRoom();
    room.state.boardAnswers.push(board(SampleJobs));
    const loaded = await room.sandbox.loadJobCounts();
    const counts = room.run("jobCounts");
    const countsLog = [...room.log];
    const logs = [];
    for (const step of [changes("j1"), changes("j2")]) {
      room.log.length = 0;
      room.state.changesAnswers.push(step);
      await room.sandbox.checkChanges();
      logs.push([...room.log]);
    }
    return { loaded: loaded, counts: counts, countsLog: countsLog, logs: logs, pending: room.run("livePending") };
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
