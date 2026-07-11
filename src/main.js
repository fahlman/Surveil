// Scanner window. No bundler: Tauri exposes its API on window.__TAURI__.
const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

const CONCURRENCY = 256;
const TIMEOUT_MS = 400;

const $ = (s) => document.querySelector(s);
const val = (s) => $(s).value;
const escapeHtml = (s) =>
  String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const fmtTime = (secs) => (secs ? new Date(secs * 1000).toLocaleString() : "");

const locationLabel = (c) =>
  c.building ? (c.area ? `${escapeHtml(c.building)} · ${escapeHtml(c.area)}` : escapeHtml(c.building)) : "—";

// ---- Building list and in-place editing ----
let config = { buildings: [] };
let selectedCidrs = new Set();
let knownCidrs = new Set();
let editingIndex = null;
let draftBuilding = null;
let addingBuilding = false;
const expanded = new Set();

const buildingCidrs = (building) => (building.ranges || []).map((range) => range.cidr).filter(Boolean);
const setConfigStatus = (message, isError = false) => {
  const status = $("#config-status");
  status.textContent = message;
  status.classList.toggle("error", isError);
};

function startEditing(index) {
  editingIndex = index;
  addingBuilding = false;
  draftBuilding = structuredClone(config.buildings[index]);
  expanded.add(draftBuilding.name);
  renderBuildings();
}

function startAdding() {
  editingIndex = config.buildings.length;
  addingBuilding = true;
  draftBuilding = { name: "", ranges: [{ name: "", cidr: "" }], notes: "" };
  renderBuildings();
}

function cancelEditing() {
  editingIndex = null;
  addingBuilding = false;
  draftBuilding = null;
  renderBuildings();
}

async function saveDraft() {
  const index = editingIndex;
  const candidate = structuredClone(config);
  const cleaned = {
    ...draftBuilding,
    name: draftBuilding.name.trim(),
    ranges: draftBuilding.ranges
      .map((range) => ({ name: range.name.trim(), cidr: range.cidr.trim() }))
      .filter((range) => range.name || range.cidr),
  };
  if (addingBuilding) candidate.buildings.push(cleaned);
  else candidate.buildings[index] = cleaned;
  try {
    await invoke("save_config", { config: candidate });
    config = candidate;
    cancelEditing();
    await loadBuildings();
    setConfigStatus(`${cleaned.name} saved.`);
  } catch (error) {
    setConfigStatus("Couldn't save: " + error, true);
  }
}

async function deleteBuilding(index) {
  const building = config.buildings[index];
  if (!building || !window.confirm(`Delete ${building.name}? This cannot be undone.`)) return;
  const candidate = { ...config, buildings: config.buildings.filter((_, i) => i !== index) };
  try {
    await invoke("save_config", { config: candidate });
    config = candidate;
    cancelEditing();
    await loadBuildings();
    setConfigStatus(`${building.name} deleted.`);
  } catch (error) {
    setConfigStatus("Couldn't delete: " + error, true);
  }
}

function renderEditingItem(item, summary, chevron, index) {
  item.classList.add("editing");
  item.open = true;
  chevron.classList.add("expanded");

  const nameInput = document.createElement("input");
  nameInput.className = "building-name-input";
  nameInput.type = "text";
  nameInput.placeholder = "Building name";
  nameInput.value = draftBuilding.name;
  nameInput.addEventListener("input", () => { draftBuilding.name = nameInput.value; });

  const save = document.createElement("button");
  save.type = "button";
  save.className = "link edit-link";
  save.textContent = "Save";
  save.addEventListener("click", saveDraft);
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "link";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", cancelEditing);
  summary.append(chevron, nameInput, save, cancel);

  const floors = document.createElement("div");
  floors.className = "b-floors editing-floors";
  draftBuilding.ranges.forEach((range, rangeIndex) => {
    const row = document.createElement("div");
    row.className = "range-row";
    const rangeName = document.createElement("input");
    rangeName.className = "range-name";
    rangeName.type = "text";
    rangeName.placeholder = "First Floor";
    rangeName.value = range.name;
    rangeName.addEventListener("input", () => { range.name = rangeName.value; });
    const cidr = document.createElement("input");
    cidr.className = "range-cidr";
    cidr.type = "text";
    cidr.placeholder = "192.168.10.0/24";
    cidr.value = range.cidr;
    cidr.addEventListener("input", () => { range.cidr = cidr.value; });
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "building-delete";
    remove.textContent = "−";
    remove.title = "Remove range";
    remove.setAttribute("aria-label", "Remove range");
    remove.addEventListener("click", () => {
      draftBuilding.ranges.splice(rangeIndex, 1);
      renderBuildings();
    });
    row.append(rangeName, cidr, remove);
    floors.appendChild(row);
  });
  const addRange = document.createElement("button");
  addRange.type = "button";
  addRange.className = "link add-floor";
  addRange.textContent = "+ Add Floor";
  addRange.addEventListener("click", () => {
    draftBuilding.ranges.push({ name: "", cidr: "" });
    renderBuildings();
  });
  floors.appendChild(addRange);
  item.append(summary, floors);
  setTimeout(() => nameInput.focus(), 0);
}

function renderBuildings() {
  const root = $("#building-list");
  root.replaceChildren();
  const buildings = addingBuilding ? config.buildings.concat(draftBuilding) : config.buildings;
  if (!buildings.length) {
    root.innerHTML = '<p class="status">No buildings yet — add one to begin.</p>';
    return;
  }

  buildings.forEach((building, index) => {
    const editing = index === editingIndex;
    const item = document.createElement("details");
    item.className = "b-item";
    item.open = editing || expanded.has(building.name);
    const summary = document.createElement("summary");
    summary.className = "b-summary";
    summary.addEventListener("click", (event) => {
      if (event.target.closest("input, button")) event.preventDefault();
    });
    const chevron = document.createElement("span");
    chevron.className = "b-chevron";
    chevron.classList.toggle("expanded", item.open);

    if (editing) {
      renderEditingItem(item, summary, chevron, index);
      root.appendChild(item);
      return;
    }

    const cidrs = buildingCidrs(building);
    const selected = cidrs.filter((cidr) => selectedCidrs.has(cidr)).length;
    chevron.classList.toggle("empty", cidrs.length === 0);
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = cidrs.length > 0 && selected === cidrs.length;
    checkbox.indeterminate = selected > 0 && selected < cidrs.length;
    checkbox.addEventListener("change", () => {
      cidrs.forEach((cidr) => checkbox.checked ? selectedCidrs.add(cidr) : selectedCidrs.delete(cidr));
      renderBuildings();
    });
    const name = document.createElement("span");
    name.className = "b-name";
    name.textContent = building.name;
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "link edit-link";
    edit.textContent = "Edit";
    edit.addEventListener("click", () => startEditing(index));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "building-delete";
    remove.textContent = "−";
    remove.title = `Delete ${building.name}`;
    remove.setAttribute("aria-label", `Delete ${building.name}`);
    remove.addEventListener("click", () => deleteBuilding(index));
    summary.append(checkbox, chevron, name, edit, remove);
    item.appendChild(summary);
    item.addEventListener("toggle", () => {
      if (item.open) expanded.add(building.name); else expanded.delete(building.name);
      chevron.classList.toggle("expanded", item.open);
    });
    const floors = document.createElement("div");
    floors.className = "b-floors";
    (building.ranges || []).forEach((range) => {
      if (!range.cidr) return;
      const row = document.createElement("label");
      row.className = "f-item";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.checked = selectedCidrs.has(range.cidr);
      checkbox.addEventListener("change", () => {
        if (checkbox.checked) selectedCidrs.add(range.cidr); else selectedCidrs.delete(range.cidr);
        renderBuildings();
      });
      const name = document.createElement("span");
      name.textContent = range.name || range.cidr;
      const cidr = document.createElement("span");
      cidr.className = "tree-cidr";
      cidr.textContent = range.cidr;
      row.append(checkbox, name, cidr);
      floors.appendChild(row);
    });
    item.appendChild(floors);
    root.appendChild(item);
  });
}

async function loadBuildings() {
  try {
    config = await invoke("get_config");
    const all = config.buildings.flatMap(buildingCidrs);
    const allSet = new Set(all);
    all.forEach((cidr) => { if (!knownCidrs.has(cidr)) selectedCidrs.add(cidr); });
    selectedCidrs.forEach((cidr) => { if (!allSet.has(cidr)) selectedCidrs.delete(cidr); });
    knownCidrs = allSet;
    renderBuildings();
  } catch (error) {
    setConfigStatus("Couldn't load buildings: " + error, true);
  }
}

$("#add-building").addEventListener("click", startAdding);

const importFile = $("#import-file");
$("#import-btn").addEventListener("click", () => importFile.click());
importFile.addEventListener("change", () => {
  const file = importFile.files[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = async () => {
    try {
      const imported = JSON.parse(reader.result);
      if (!imported || !Array.isArray(imported.buildings)) throw new Error("not a Surveil configuration");
      await invoke("save_config", { config: imported });
      cancelEditing();
      await loadBuildings();
      setConfigStatus(`Imported ${imported.buildings.length} buildings.`);
    } catch (error) {
      setConfigStatus("Import failed: " + error, true);
    } finally {
      importFile.value = "";
    }
  };
  reader.readAsText(file);
});

$("#export-btn").addEventListener("click", async () => {
  try {
    const path = await invoke("export_config");
    if (path) setConfigStatus("Exported configuration.");
  } catch (error) {
    setConfigStatus("Export failed: " + error, true);
  }
});

// ---- Scan (selected buildings/floors) ----
const scanBtn = $("#scan-btn");
const progressWrap = $("#progress-wrap");
const progressBar = $("#progress-bar");
const progressText = $("#progress-text");
const summaryEl = $("#summary");
const results = $("#results");
const resultsBody = $("#results tbody");

listen("scan:progress", (e) => {
  const { scanned, total, found } = e.payload;
  const pct = total ? Math.round((scanned / total) * 100) : 0;
  progressBar.style.width = `${pct}%`;
  progressText.textContent =
    `Scanned ${scanned.toLocaleString()} / ${total.toLocaleString()} · found ${found}`;
});

const STATUS_BADGE = {
  new: '<span class="badge new">NEW</span>',
  present: '<span class="badge ok">online</span>',
  absent: '<span class="badge absent">missing</span>',
};

function renderResults(cameras) {
  const online = cameras.filter((c) => c.status !== "absent").length;
  const added = cameras.filter((c) => c.status === "new").length;
  const missing = cameras.filter((c) => c.status === "absent").length;
  summaryEl.textContent = `${online} online · ${added} new · ${missing} missing`;
  summaryEl.classList.remove("error");

  resultsBody.replaceChildren();
  cameras.forEach((c) => {
    const tr = document.createElement("tr");
    tr.className = c.status;
    const lastSeen = c.status === "absent" ? `last seen ${fmtTime(c.last_seen)}` : "";
    tr.innerHTML =
      `<td>${STATUS_BADGE[c.status] || c.status}</td>` +
      `<td>${locationLabel(c)}</td>` +
      `<td>${c.ip}</td>` +
      `<td>${lastSeen}</td>`;
    resultsBody.appendChild(tr);
  });
  results.hidden = cameras.length === 0;
}

scanBtn.addEventListener("click", async () => {
  const targets = [...selectedCidrs];
  if (!targets.length) {
    summaryEl.textContent = "Select at least one building or floor to scan.";
    summaryEl.classList.add("error");
    return;
  }
  const port = parseInt(val("#port"), 10) || 80;

  scanBtn.disabled = true;
  resultsBody.replaceChildren();
  results.hidden = true;
  summaryEl.textContent = "";
  summaryEl.classList.remove("error");
  progressWrap.hidden = false;
  progressBar.style.width = "0%";
  progressText.textContent = "Starting…";

  try {
    const cameras = await invoke("scan", {
      targets,
      port,
      concurrency: CONCURRENCY,
      timeout: TIMEOUT_MS,
    });
    renderResults(cameras);
  } catch (e) {
    summaryEl.textContent = "Error: " + e;
    summaryEl.classList.add("error");
  } finally {
    scanBtn.disabled = false;
  }
});

// ---- WS-Discovery quick scan (diagnostic) ----
const wsdBtn = $("#wsd-btn");
const wsdResult = $("#wsd-result");
const wsdVerdict = $("#wsd-verdict");
const wsdTable = $("#wsd-table");
const wsdBody = $("#wsd-table tbody");

wsdBtn.addEventListener("click", async () => {
  wsdBtn.disabled = true;
  wsdResult.hidden = false;
  wsdTable.hidden = true;
  wsdBody.replaceChildren();
  wsdVerdict.classList.remove("error");
  wsdVerdict.textContent = "Probing 239.255.255.250:3702 for ONVIF cameras…";
  try {
    const r = await invoke("ws_discover", { timeout: 4000 });
    renderWsd(r);
  } catch (e) {
    wsdVerdict.textContent = "Error: " + e;
    wsdVerdict.classList.add("error");
  } finally {
    wsdBtn.disabled = false;
  }
});

function renderWsd(r) {
  const n = r.responders.length;
  let verdict;
  if (n === 0) {
    verdict =
      "No ONVIF devices answered — either nothing's listening here, the probe was blocked, or multicast isn't forwarded across subnets.";
  } else if (r.distinct_subnets <= 1) {
    verdict = `${n} device${n === 1 ? "" : "s"} answered, all on one subnet — multicast forwarding looks OFF (only your local subnet replied).`;
  } else {
    verdict = `${n} devices answered across ${r.distinct_subnets} subnets — multicast forwarding is ON.`;
  }
  wsdVerdict.textContent = verdict;
  wsdBody.replaceChildren();
  r.responders.forEach((c) => {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${locationLabel(c)}</td><td>${c.ip}</td><td>${escapeHtml(c.xaddrs)}</td>`;
    wsdBody.appendChild(tr);
  });
  wsdTable.hidden = n === 0;
}

loadBuildings();
