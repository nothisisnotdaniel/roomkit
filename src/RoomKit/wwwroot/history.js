"use strict";

// History: the finished factory jobs from /api/history, newest first, with token totals per project and per mode for
// the last 7 and 30 days, an All time block (filtered by the Project select) and a daily chart of tokens per mode.
// Read-only. A job without token data is "unknown", never 0. Loaded after capture.js and before live.js and app.js;
// their functions (element, asList, asText, asNumber, titleText, jobHash, formatTokens, authFetch) are only called
// at run time. Every text is set with textContent and the chart is built with createElementNS, never from markup.

const HistoryTexts = {
  loading: "Loading…",
  failed: "Could not load history.",
  empty: "No finished jobs yet.",
  emptyProject: "No jobs for this project.",
  noRecent: "No finished jobs in the last 30 days.",
  last7: "Last 7 days",
  last30: "Last 30 days",
  allTime: "All time",
  projects: "Projects",
  modes: "Modes",
  name: "Name",
  jobs: "Jobs",
  tokens: "Tokens",
  chart: "Tokens per day",
  chartLabel: "Tokens per day by mode over the last 30 days",
  chartNone: "no known tokens",
  listTitle: "Finished jobs",
  projectLabel: "Project",
  allProjects: "All projects",
  noProject: "No project",
  noMode: "No mode",
  unknown: "unknown",
  round: "Round ",
};

const HistoryOutcomes = { merged: "Merged", escalated: "Escalated", reverted: "Reverted", findings: "Findings" };

const HistoryRoute = "#history";
const HistoryApi = "/api/history";
const HistoryColourCount = 6;
const SvgNamespace = "http://www.w3.org/2000/svg";
// The chart's own units: one 10-unit slot per day (an 8-unit bar and a gap), ChartHeight units for the busiest day.
const ChartSlot = 10;
const ChartBar = 8;
const ChartHeight = 100;

let historyRequest = 0;
// The last good answer, so a project change filters it again without a fetch.
let historyData = null;

function historyShown() {
  return !document.getElementById("history").hidden;
}

function storedHistoryProject() {
  try {
    return localStorage.getItem(HistoryProjectKey) || "";
  } catch {
    return "";
  }
}

function storeHistoryProject(name) {
  try {
    localStorage.setItem(HistoryProjectKey, name);
  } catch {
    // Without storage the filter is simply not kept.
  }
}

function asNameOrNull(value) {
  return typeof value === "string" ? value : null;
}

function readHistoryWindow(value) {
  return {
    jobs: asNumber(value && value.jobs) || 0,
    tokens: asNumber(value && value.tokens) || 0,
    unknown: asNumber(value && value.unknown) || 0,
  };
}

function readHistoryTotals(list) {
  return asList(list).map((item) => ({
    name: asNameOrNull(item && item.name),
    last7: readHistoryWindow(item && item.last7),
    last30: readHistoryWindow(item && item.last30),
  }));
}

function readHistoryLifetimeTotals(list) {
  return asList(list).map((item) => ({
    name: asNameOrNull(item && item.name),
    all: readHistoryWindow(item && item.all),
  }));
}

// The lifetime totals; a missing or malformed lifetime gives empty lists, so the rest of the view still shows.
function readHistoryLifetime(value) {
  const lifetime = value && typeof value === "object" ? value : {};
  return {
    projects: readHistoryLifetimeTotals(lifetime.projects),
    modes: readHistoryLifetimeTotals(lifetime.modes),
    projectModes: asList(lifetime.projectModes).map((item) => ({
      name: asNameOrNull(item && item.name),
      modes: readHistoryLifetimeTotals(item && item.modes),
    })),
  };
}

// The response with only well-formed fields, or null when it has another shape.
function readHistory(data) {
  if (!data || !Array.isArray(data.jobs) || !Array.isArray(data.days) || !data.totals || !data.chart) {
    return null;
  }
  const jobs = [];
  for (const item of data.jobs) {
    const id = asText(item && item.id);
    if (id === "" || typeof item.outcome !== "string" || !Object.hasOwn(HistoryOutcomes, item.outcome)) {
      continue;
    }
    jobs.push({
      id: id,
      project: asNameOrNull(item.project),
      title: asText(item.title),
      mode: asNameOrNull(item.mode),
      path: asNameOrNull(item.path),
      round: asNumber(item.round),
      outcome: item.outcome,
      tokens: asNumber(item.tokens),
    });
  }
  const days = data.days.filter((day) => typeof day === "string");
  const chart = asList(data.chart.modes).map((item) => ({
    name: asNameOrNull(item && item.name),
    tokens: days.map((_, index) => asNumber(asList(item && item.tokens)[index]) || 0),
  }));
  return {
    jobs: jobs,
    days: days,
    projects: readHistoryTotals(data.totals.projects),
    modes: readHistoryTotals(data.totals.modes),
    chart: chart,
    lifetime: readHistoryLifetime(data.lifetime),
  };
}

function historyName(name, missing) {
  return name === null ? missing : name;
}

// Known tokens, plus "+ k unknown" when some jobs have none, so unknown never reads as 0.
function historyTokens(total) {
  const known = formatTokens(total.tokens);
  return total.unknown > 0 ? known + " + " + total.unknown + " " + HistoryTexts.unknown : known;
}

// Builds the fixed parts once: a status line, the summary, the project filter and the list.
function buildHistoryView() {
  const main = document.getElementById("history");
  if (document.getElementById("history-status")) {
    return;
  }
  const status = element("p", "status history-status", HistoryTexts.loading);
  status.id = "history-status";
  const summary = element("div", "history-summary");
  summary.id = "history-summary";

  const list = element("section", "history-jobs");
  list.id = "history-jobs";
  list.setAttribute("aria-labelledby", "history-jobs-title");
  const heading = element("h2", "history-heading", HistoryTexts.listTitle);
  heading.id = "history-jobs-title";
  const label = element("label", "history-label", HistoryTexts.projectLabel);
  label.htmlFor = "history-project";
  const select = element("select", "history-select");
  select.id = "history-project";
  select.addEventListener("change", () => {
    storeHistoryProject(select.value);
    renderHistoryAll();
    renderHistoryList();
  });
  const rows = element("div", "history-rows");
  rows.id = "history-rows";
  list.append(heading, label, select, rows);

  main.replaceChildren(status, summary, list);
  summary.hidden = true;
  list.hidden = true;
}

function showHistoryStatus(text, className) {
  const status = document.getElementById("history-status");
  status.className = className + " history-status";
  status.textContent = text;
  status.hidden = text === "";
}

// Fetches the history once and renders it while the view is shown. Returns false when it failed.
async function loadHistory() {
  historyRequest += 1;
  const request = historyRequest;
  let data;
  try {
    const response = await authFetch(HistoryApi, { cache: "no-store" });
    if (!response.ok) {
      throw new Error("status");
    }
    data = readHistory(await response.json());
    if (data === null) {
      throw new Error("data");
    }
  } catch {
    if (request === historyRequest && historyShown()) {
      showHistoryStatus(HistoryTexts.failed, "error");
      document.getElementById("history-summary").hidden = true;
      document.getElementById("history-jobs").hidden = true;
    }
    return false;
  }
  if (request !== historyRequest) {
    return true;
  }
  historyData = data;
  if (historyShown()) {
    const scroll = window.scrollY;
    renderHistory();
    window.scrollTo(0, scroll);
  }
  return true;
}

function renderHistory() {
  const data = historyData;
  const summary = document.getElementById("history-summary");
  const list = document.getElementById("history-jobs");
  if (data.jobs.length === 0) {
    showHistoryStatus(HistoryTexts.empty, "empty");
    summary.hidden = true;
    list.hidden = true;
    return;
  }
  showHistoryStatus("", "status");
  const nodes = [];
  const all = element("div");
  all.id = "history-all-block";
  const recent = data.projects.length > 0 || data.modes.length > 0;
  if (!recent) {
    nodes.push(element("p", "empty history-recent", HistoryTexts.noRecent));
  } else {
    nodes.push(renderHistoryWindow("history-last7", HistoryTexts.last7, data, "last7"));
    nodes.push(renderHistoryWindow("history-last30", HistoryTexts.last30, data, "last30"));
  }
  nodes.push(all);
  if (recent) {
    nodes.push(renderHistoryChart(data));
  }
  summary.replaceChildren(...nodes);
  summary.hidden = false;
  list.hidden = false;
  fillHistoryProjects(data.jobs);
  renderHistoryAll();
  renderHistoryList();
}

// The lifetime lists for the chosen project: its own row and its projectModes entry, or every row for All projects.
function historyLifetimeFor(lifetime, chosen) {
  if (chosen === "") {
    return { projects: lifetime.projects, modes: lifetime.modes };
  }
  const entry = lifetime.projectModes.find((item) => item.name === chosen);
  return {
    projects: lifetime.projects.filter((item) => item.name === chosen),
    modes: entry ? entry.modes : [],
  };
}

// The All time block, re-rendered from the stored answer when the Project select changes; hidden without lifetime
// data.
function renderHistoryAll() {
  const all = document.getElementById("history-all-block");
  if (!all || historyData === null) {
    return;
  }
  const lifetime = historyData.lifetime;
  if (lifetime.projects.length === 0 && lifetime.modes.length === 0) {
    all.replaceChildren();
    all.hidden = true;
    return;
  }
  const chosen = document.getElementById("history-project").value;
  const shown = historyLifetimeFor(lifetime, chosen);
  all.replaceChildren(renderHistoryWindow("history-all", HistoryTexts.allTime, shown, "all"));
  all.hidden = false;
}

// One window: a projects table and a modes table.
function renderHistoryWindow(id, title, data, key) {
  const section = element("section", "history-window");
  section.setAttribute("aria-labelledby", id);
  const heading = element("h2", "history-heading", title);
  heading.id = id;
  section.append(
    heading,
    renderHistoryTable(HistoryTexts.projects, data.projects, key, HistoryTexts.noProject),
    renderHistoryTable(HistoryTexts.modes, data.modes, key, HistoryTexts.noMode));
  return section;
}

function renderHistoryTable(caption, totals, key, missing) {
  const table = element("table", "history-table");
  table.appendChild(element("caption", "history-caption", caption));
  const head = element("thead");
  const headRow = element("tr");
  for (const text of [HistoryTexts.name, HistoryTexts.jobs, HistoryTexts.tokens]) {
    const cell = element("th", "", text);
    cell.scope = "col";
    headRow.appendChild(cell);
  }
  head.appendChild(headRow);
  const body = element("tbody");
  for (const total of totals) {
    const row = element("tr");
    const name = element("th", "", historyName(total.name, missing));
    name.scope = "row";
    row.append(
      name,
      element("td", "history-number", String(total[key].jobs)),
      element("td", "history-number", historyTokens(total[key])));
    body.appendChild(row);
  }
  table.append(head, body);
  return table;
}

function svgElement(tag, attributes) {
  const node = document.createElementNS(SvgNamespace, tag);
  for (const [name, value] of Object.entries(attributes)) {
    node.setAttribute(name, String(value));
  }
  return node;
}

function historyColour(index) {
  return "history-colour-" + (index % HistoryColourCount);
}

// One stacked bar per day, one colour per mode, scaled to the busiest day. The legend gives each mode's name and
// 30-day total as text, so the chart can be read without colour.
function renderHistoryChart(data) {
  const section = element("section", "history-chart");
  section.setAttribute("aria-labelledby", "history-chart-title");
  const heading = element("h2", "history-heading", HistoryTexts.chart);
  heading.id = "history-chart-title";
  section.appendChild(heading);

  const sums = data.days.map((_, day) => data.chart.reduce((sum, mode) => sum + mode.tokens[day], 0));
  const busiest = Math.max(0, ...sums);
  const width = data.days.length * ChartSlot;
  const svg = svgElement("svg", {
    class: "history-svg",
    viewBox: "0 0 " + width + " " + ChartHeight,
    preserveAspectRatio: "none",
    role: "img",
  });
  const totals = data.chart.map((mode) => mode.tokens.reduce((sum, value) => sum + value, 0));
  const described = data.chart.map((mode, index) =>
    historyName(mode.name, HistoryTexts.noMode) + " " + formatTokens(totals[index]));
  svg.setAttribute("aria-label", HistoryTexts.chartLabel + ": "
    + (described.length > 0 && busiest > 0 ? described.join(", ") : HistoryTexts.chartNone) + ".");
  svg.appendChild(svgElement("line", {
    class: "history-axis", x1: 0, y1: ChartHeight, x2: width, y2: ChartHeight,
  }));
  if (busiest > 0) {
    data.days.forEach((_, day) => {
      let top = ChartHeight;
      data.chart.forEach((mode, index) => {
        const value = mode.tokens[day];
        if (value <= 0) {
          return;
        }
        const height = (value / busiest) * ChartHeight;
        top -= height;
        svg.appendChild(svgElement("rect", {
          class: "history-bar " + historyColour(index),
          x: day * ChartSlot + (ChartSlot - ChartBar) / 2,
          y: top,
          width: ChartBar,
          height: height,
        }));
      });
    });
  }
  section.appendChild(svg);

  if (data.days.length > 0) {
    const range = element("p", "history-range");
    range.append(element("span", "", data.days[0]), element("span", "", data.days[data.days.length - 1]));
    section.appendChild(range);
  }

  const legend = element("ul", "history-legend");
  data.chart.forEach((mode, index) => {
    const item = element("li", "history-legend-item");
    const swatch = element("span", "history-swatch " + historyColour(index));
    swatch.setAttribute("aria-hidden", "true");
    item.append(
      swatch,
      element("span", "history-legend-name", historyName(mode.name, HistoryTexts.noMode)),
      element("span", "history-legend-total", formatTokens(totals[index])));
    legend.appendChild(item);
  });
  section.appendChild(legend);
  return section;
}

// All projects plus every project in the list; the chosen one is kept from localStorage, else All projects.
function fillHistoryProjects(jobs) {
  const select = document.getElementById("history-project");
  const names = [...new Set(jobs.map((job) => job.project).filter((name) => name !== null))]
    .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  const wanted = select.options.length > 0 ? select.value : storedHistoryProject();
  const options = [element("option", "", HistoryTexts.allProjects)];
  options[0].value = "";
  for (const name of names) {
    const option = element("option", "", name);
    option.value = name;
    options.push(option);
  }
  select.replaceChildren(...options);
  select.value = names.includes(wanted) ? wanted : "";
}

// The list for the chosen project, filtered here; the 7/30-day totals and chart always show every project.
function renderHistoryList() {
  const rows = document.getElementById("history-rows");
  if (historyData === null) {
    return;
  }
  const chosen = document.getElementById("history-project").value;
  const jobs = historyData.jobs.filter((job) => chosen === "" || job.project === chosen);
  if (jobs.length === 0) {
    rows.replaceChildren(element("p", "empty", HistoryTexts.emptyProject));
    return;
  }
  const list = element("ul", "lines history-list");
  for (const job of jobs) {
    list.appendChild(renderHistoryJob(job));
  }
  rows.replaceChildren(list);
}

function renderHistoryJob(job) {
  const row = element("li", "history-item");
  row.appendChild(element("span", "history-project", historyName(job.project, HistoryTexts.noProject)));
  const title = element("a", "history-title", titleText(job.title));
  title.href = jobHash(job.id);
  row.appendChild(title);
  const facts = element("span", "history-facts");
  for (const text of [job.mode, job.path, job.round === null ? null : HistoryTexts.round + job.round]) {
    if (text !== null && text !== "") {
      facts.appendChild(element("span", "history-fact", text));
    }
  }
  facts.appendChild(element("span", "badge history-outcome history-outcome-" + job.outcome,
    HistoryOutcomes[job.outcome]));
  facts.appendChild(element("span", "history-tokens",
    job.tokens === null ? HistoryTexts.unknown : formatTokens(job.tokens)));
  row.appendChild(facts);
  return row;
}

// Called by app.js when the history view is shown (its heading is the header title): the last answer stays while
// it is fetched again, so nothing flashes.
function showHistory() {
  buildHistoryView();
  if (historyData !== null) {
    renderHistory();
  }
  loadHistory();
}
