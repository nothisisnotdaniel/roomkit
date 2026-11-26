"use strict";

// Search at the top of the Backlogs view (#133): finds any backlog item across all projects through /api/search by
// number (#12, Times#12), by words in the title or rest, or by words in its story (searched on the server; story
// text never comes to the page), with a mark, a project and a category filter. One request per field or filter
// change, no timer; an answer for an older request is dropped. The form is built once into #backlog-search and
// kept, so Back from a story finds the same text, filters and results. While a query (field text or a filter) is
// entered, #backlogs gets the class "searching" and only the search box shows, so the results stand in place of
// the backlog; with an empty field and no filter the backlog shows again as it was.
// Loaded after tasks.js and before live.js and app.js; their functions (element, titleText, storyHash, openStory,
// asText, asNumber, asList, categoryChip, Categories) are only called at run time. Read-only: no edit buttons. Only fixed texts and file text
// through textContent are shown, never server error text.

const SearchTexts = {
  label: "Search backlogs",
  markLabel: "Mark",
  projectLabel: "Project",
  categoryLabel: "Category",
  allCategories: "All categories",
  allMarks: "All marks",
  allProjects: "All projects",
  hint: "Type a number, title or word.",
  searching: "Searching…",
  none: "No items found.",
  showing: "Showing ",
  of: " of ",
  failed: "Can't search right now.",
  unreadable: ": backlog can't be read.",
  foundInStory: "Found in story",
};

// The mark filter's options: value and label.
const SearchMarkOptions = [
  ["", SearchTexts.allMarks],
  ["open", "Open"],
  ["approval", "Proposed"],
  ["task", "Tasks"],
  ["done", "Done"],
];

// The mark label on each result.
const SearchMarkLabels = { open: "Open", approval: "Proposed", task: "Task", done: "Done" };

const SearchMatches = ["number", "title", "story", "filter"];

// The old Search tab: app.js opens the Backlogs with the field focused, so bookmarks keep working.
const SearchRoute = "#search";
const SearchApi = "/api/search";
const SearchingClass = "searching";

let searchRequest = 0;
// Set by live.js when a backlog or story changed while the view was hidden: the next show searches again.
let searchStale = false;
let searchProjectsFilled = false;

// The search box lives in the Backlogs view, so it is shown while Backlogs is.
function searchShown() {
  return !document.getElementById("backlogs").hidden;
}

function backlogSearchBox() {
  return document.getElementById("backlog-search");
}

// Results in place of the backlog while a query is entered; the backlog again when there is none.
function showSearching(searching) {
  document.getElementById("backlogs").classList.toggle(SearchingClass, searching);
}

function searchField() {
  return document.getElementById("search-field");
}

function searchMark() {
  return document.getElementById("search-mark");
}

function searchProject() {
  return document.getElementById("search-project");
}

function searchCategory() {
  return document.getElementById("search-category");
}

function searchLabel(text, target) {
  const label = element("label", "search-label", text);
  label.htmlFor = target;
  return label;
}

function searchOption(value, text) {
  const option = element("option", null, text);
  option.value = value;
  return option;
}

// The form and the result area, built once on the first show; the field, filters and results stay in the page.
function buildSearchView() {
  const box = backlogSearchBox();
  const form = element("form", "search-form");
  form.setAttribute("role", "search");
  form.noValidate = true;
  form.addEventListener("submit", (event) => event.preventDefault());

  const field = element("input", "search-field");
  field.id = "search-field";
  field.type = "search";
  field.autocomplete = "off";
  field.setAttribute("autocapitalize", "off");
  field.spellcheck = false;
  field.setAttribute("enterkeyhint", "search");
  field.maxLength = 200;
  field.placeholder = SearchTexts.hint;
  field.addEventListener("input", () => runSearch(true));
  form.appendChild(searchLabel(SearchTexts.label, "search-field"));
  form.appendChild(field);

  const filters = element("div", "search-filters");
  const mark = element("select", "search-select");
  mark.id = "search-mark";
  for (const [value, text] of SearchMarkOptions) {
    mark.appendChild(searchOption(value, text));
  }
  mark.addEventListener("change", () => runSearch(true));
  const project = element("select", "search-select");
  project.id = "search-project";
  project.appendChild(searchOption("", SearchTexts.allProjects));
  project.addEventListener("change", () => runSearch(true));
  const category = element("select", "search-select");
  category.id = "search-category";
  category.appendChild(searchOption("", SearchTexts.allCategories));
  for (const name of Categories) {
    category.appendChild(searchOption(name, name));
  }
  category.addEventListener("change", () => runSearch(true));
  const markBox = element("div", "search-filter");
  markBox.append(searchLabel(SearchTexts.markLabel, "search-mark"), mark);
  const projectBox = element("div", "search-filter");
  projectBox.append(searchLabel(SearchTexts.projectLabel, "search-project"), project);
  const categoryBox = element("div", "search-filter");
  categoryBox.append(searchLabel(SearchTexts.categoryLabel, "search-category"), category);
  filters.append(markBox, projectBox, categoryBox);
  form.appendChild(filters);

  const status = element("p", "status search-status");
  status.id = "search-status";
  status.setAttribute("role", "status");
  const results = element("div", "search-results");
  results.id = "search-results";
  box.replaceChildren(form, status, results);
}

function showSearchStatus(text, className) {
  const status = document.getElementById("search-status");
  status.className = (className || "status") + " search-status";
  status.textContent = text;
  status.hidden = text === "";
}

// Only the results are replaced, so the field keeps its text and focus.
function showSearchResults(nodes) {
  document.getElementById("search-results").replaceChildren(...nodes);
}

// The project filter gets the names of the first answer once; a chosen project stays chosen.
function fillSearchProjects(names) {
  if (searchProjectsFilled) {
    return;
  }
  searchProjectsFilled = true;
  const select = searchProject();
  for (const name of names) {
    select.appendChild(searchOption(name, name));
  }
}

// One item with only well-formed fields, or null.
function readSearchItem(item) {
  const project = asText(item && item.project);
  const mark = asText(item && item.mark);
  const match = asText(item && item.match);
  if (project === "" || !(mark in SearchMarkLabels) || !SearchMatches.includes(match)) {
    return null;
  }
  return {
    project: project,
    number: asNumber(item.number),
    mark: mark,
    title: asText(item.title),
    rest: asText(item.rest),
    story: item.story === true,
    match: match,
    category: typeof item.category === "string" && Categories.includes(item.category) ? item.category : null,
  };
}

// The response as { items, total, projects, unreadable }, or null when it has another shape.
function readSearch(data) {
  if (!data || !Array.isArray(data.items) || asNumber(data.total) === null || !Array.isArray(data.projects)
    || !Array.isArray(data.unreadable)) {
    return null;
  }
  const names = (list) => list.filter((name) => typeof name === "string" && name !== "");
  return {
    items: data.items.map(readSearchItem).filter((item) => item !== null),
    total: data.total,
    projects: names(data.projects),
    unreadable: names(data.unreadable),
  };
}

function searchUrl(text, mark, project, category) {
  let url = SearchApi + "?q=" + encodeURIComponent(text);
  if (mark !== "") {
    url += "&mark=" + encodeURIComponent(mark);
  }
  if (project !== "") {
    url += "&project=" + encodeURIComponent(project);
  }
  if (category !== "") {
    url += "&category=" + encodeURIComponent(category);
  }
  return url;
}

// One request for the current field and filters. With an empty field and no filter nothing is sent and the hint is
// shown. A newer request (or the hint) makes an older answer count for nothing. Searching… is shown only for a change
// the user made (progress), not for a live re-run. Returns false when it failed.
async function runSearch(progress) {
  searchRequest += 1;
  const request = searchRequest;
  const text = searchField().value.trim();
  const mark = searchMark().value;
  const project = searchProject().value;
  const category = searchCategory().value;
  if (text === "" && mark === "" && project === "" && category === "") {
    showSearchStatus("");
    showSearchResults([]);
    showSearching(false);
    return true;
  }
  showSearching(true);
  if (progress) {
    showSearchStatus(SearchTexts.searching);
  }
  let result;
  try {
    const response = await authFetch(searchUrl(text, mark, project, category), { cache: "no-store" });
    if (!response.ok) {
      throw new Error("status");
    }
    result = readSearch(await response.json());
    if (result === null) {
      throw new Error("data");
    }
  } catch {
    // A failed live re-run keeps the results shown; live.js tries again on its next check.
    if (request === searchRequest && progress) {
      showSearchStatus("");
      showSearchResults([element("p", "error", SearchTexts.failed)]);
    }
    return false;
  }
  if (request !== searchRequest) {
    return true;
  }
  fillSearchProjects(result.projects);
  const scroll = window.scrollY;
  renderSearch(result);
  if (searchShown()) {
    window.scrollTo(0, scroll);
  }
  return true;
}

function renderSearch(result) {
  const nodes = [];
  for (const name of result.unreadable) {
    nodes.push(element("p", "error search-unreadable", name + SearchTexts.unreadable));
  }
  if (result.items.length === 0) {
    showSearchStatus("");
    nodes.push(element("p", "empty", SearchTexts.none));
    showSearchResults(nodes);
    return;
  }
  if (result.total > result.items.length) {
    showSearchStatus(SearchTexts.showing + result.items.length + SearchTexts.of + result.total + ".");
  } else {
    showSearchStatus("");
  }
  const list = element("ul", "lines search-list");
  for (const item of result.items) {
    list.appendChild(renderSearchItem(item));
  }
  nodes.push(list);
  showSearchResults(nodes);
}

function renderSearchItem(item) {
  const row = element("li", "search-item");
  const meta = element("span", "search-meta");
  meta.appendChild(element("span", "search-project", item.project));
  meta.appendChild(element("span", "badge search-mark search-mark-" + item.mark, SearchMarkLabels[item.mark]));
  const chip = categoryChip(item.category);
  if (chip !== null) {
    meta.appendChild(chip);
  }
  row.appendChild(meta);
  const label = (item.number !== null ? "#" + item.number + " " : "") + titleText(item.title);
  // Only a line with a numbered story link opens a story.
  if (item.number !== null && item.story) {
    row.classList.add("numbered");
    const head = element("a", "head search-head", label);
    head.href = storyHash(item.project, item.number);
    head.addEventListener("click", (event) => {
      event.preventDefault();
      openStory(item.project, item.number);
    });
    row.appendChild(head);
  } else {
    row.appendChild(element("strong", "title search-head", label));
  }
  if (item.rest !== "") {
    row.appendChild(element("span", "rest", item.rest));
  }
  if (item.match === "story") {
    row.appendChild(element("span", "search-found", SearchTexts.foundInStory));
  }
  return row;
}

// Called by app.js whenever Backlogs is shown: built on the first show, otherwise kept as it was (searched again
// first when a backlog or story changed while it was hidden).
function showSearch() {
  if (searchField() === null) {
    buildSearchView();
    runSearch(true);
    return;
  }
  if (searchStale) {
    searchStale = false;
    refreshSearch();
  }
}

// Called by app.js for the old #search link: the field gets focus, the page stays at the top.
function focusSearch() {
  searchField().focus({ preventScroll: true });
}

// Live refresh (live.js): a changed backlog or story searches again while Backlogs is shown, replacing only the
// results; while hidden, the next show does. Returns false when it should be tried again.
async function refreshSearch() {
  if (searchField() === null) {
    return true;
  }
  if (!searchShown()) {
    searchStale = true;
    return true;
  }
  return runSearch(false);
}
