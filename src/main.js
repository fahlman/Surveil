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

// ---- Building list: check a whole building or expand to pick floors ----
let config = { buildings: [] };
let editingIndex = null;
let selectedCidrs = new Set();
let knownCidrs = new Set();
const expanded = new Set(); // building names currently expanded

const buildingCidrs = (b) => (b.ranges || []).map((r) => r.cidr).filter(Boolean);

function renderBuildings() {
  const root = $("#building-list");
  const editor = $("#building-editor");
  const editorHome = $("#editor-home");
  if (editor && editorHome && editor.parentElement !== editorHome) editorHome.appendChild(editor);
  root.replaceChildren();
  if (!config.buildings.length) {
    root.innerHTML =
      '<p class="status">No buildings yet — add one to begin.</p>';
    return;
  }

  config.buildings.forEach((b, index) => {
    const cidrs = buildingCidrs(b);
    const selected = cidrs.filter((c) => selectedCidrs.has(c)).length;

    const item = document.createElement("details");
    item.className = "b-item";
    item.dataset.buildingIndex = index;
    item.open = expanded.has(b.name);

    const summary = document.createElement("summary");
    summary.className = "b-summary";
    // A click on the checkbox or Edit button shouldn't toggle the expander.
    summary.addEventListener("click", (e) => {
      if (e.target.closest("input, button")) e.preventDefault();
    });

    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = cidrs.length > 0 && selected === cidrs.length;
    cb.indeterminate = selected > 0 && selected < cidrs.length;
    cb.addEventListener("change", () => {
      if (cb.checked) cidrs.forEach((c) => selectedCidrs.add(c));
      else cidrs.forEach((c) => selectedCidrs.delete(c));
      renderBuildings();
    });

    const chevron = document.createElement("span");
    chevron.className = "b-chevron";
    chevron.classList.toggle("expanded", item.open);
    chevron.classList.toggle("empty", cidrs.length === 0);

    const name = document.createElement("span");
    name.className = "b-name";
    name.textContent = b.name;

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "link edit-link";
    edit.textContent = "Edit";
    edit.addEventListener("click", () => openEditor(index));

    summary.append(cb, chevron, name, edit);
    item.appendChild(summary);

    item.addEventListener("toggle", () => {
      if (item.open) expanded.add(b.name);
      else expanded.delete(b.name);
      chevron.classList.toggle("expanded", item.open);
    });

    const floors = document.createElement("div");
    floors.className = "b-floors";
    (b.ranges || []).forEach((r) => {
      if (!r.cidr) return;
      const fl = document.createElement("label");
      fl.className = "f-item";
      const fcb = document.createElement("input");
      fcb.type = "checkbox";
      fcb.checked = selectedCidrs.has(r.cidr);
      fcb.addEventListener("change", () => {
        if (fcb.checked) selectedCidrs.add(r.cidr);
        else selectedCidrs.delete(r.cidr);
        renderBuildings();
      });
      const fn = document.createElement("span");
      fn.textContent = r.name || r.cidr;
      const fc = document.createElement("span");
      fc.className = "tree-cidr";
      fc.textContent = r.cidr;
      fl.append(fcb, fn, fc);
      floors.appendChild(fl);
    });
    item.appendChild(floors);
    if (editingIndex === index && !editor.hidden) item.appendChild(editor);
    root.appendChild(item);
  });
}

async function loadBuildings() {
  try {
    config = await invoke("get_config");
    const all = config.buildings.flatMap(buildingCidrs);
    const allSet = new Set(all);
    all.forEach((c) => {
      if (!knownCidrs.has(c)) selectedCidrs.add(c); // new ranges default to selected
    });
    selectedCidrs.forEach((c) => {
      if (!allSet.has(c)) selectedCidrs.delete(c);
    });
    knownCidrs = allSet;
    renderBuildings();
  } catch (e) {
    console.error("get_config failed", e);
  }
}

// ---- Inline building configuration ----
const editor = $("#building-editor");
const editorStatus = $("#editor-status");
const setConfigStatus = (message, isError = false) => {
  const status = $("#config-status");
  status.textContent = message;
  status.classList.toggle("error", isError);
};

function addRangeRow(name = "", cidr = "") {
  const row = document.createElement("div");
  row.className = "range-row";
  const nameInput = document.createElement("input");
  nameInput.className = "range-name";
  nameInput.type = "text";
  nameInput.placeholder = "First Floor";
  nameInput.value = name;
  const cidrInput = document.createElement("input");
  cidrInput.className = "range-cidr";
  cidrInput.type = "text";
  cidrInput.placeholder = "192.168.10.0/24";
  cidrInput.value = cidr;
  const remove = document.createElement("button");
  remove.className = "link-danger";
  remove.type = "button";
  remove.textContent = "Remove";
  remove.addEventListener("click", () => row.remove());
  row.append(nameInput, cidrInput, remove);
  $("#range-list").appendChild(row);
}

function openEditor(index = null) {
  editingIndex = index;
  const building = index === null ? null : config.buildings[index];
  $("#editor-title").textContent = building ? `Edit ${building.name}` : "Add Building";
  $("#building-name").value = building?.name || "";
  $("#range-list").replaceChildren();
  const ranges = building?.ranges?.length ? building.ranges : [{ name: "", cidr: "" }];
  ranges.forEach((range) => addRangeRow(range.name || "", range.cidr || ""));
  $("#save-building").textContent = building ? "Save Changes" : "Add Building";
  $("#delete-building").hidden = !building;
  editorStatus.textContent = "";
  editorStatus.classList.remove("error");
  editor.hidden = false;
  if (building) {
    expanded.add(building.name);
    const item = document.querySelector(`[data-building-index="${index}"]`);
    if (item) {
      item.open = true;
      item.appendChild(editor);
    }
  } else {
    $("#editor-home").appendChild(editor);
  }
  editor.scrollIntoView({ behavior: "smooth", block: "nearest" });
  $("#building-name").focus();
}

function closeEditor() {
  editingIndex = null;
  editor.hidden = true;
  $("#editor-home").appendChild(editor);
}

const readRanges = () =>
  Array.from(document.querySelectorAll("#range-list .range-row"))
    .map((row) => ({
      name: row.querySelector(".range-name").value.trim(),
      cidr: row.querySelector(".range-cidr").value.trim(),
    }))
    .filter((range) => range.name || range.cidr);

$("#add-building").addEventListener("click", () => openEditor());
$("#add-range").addEventListener("click", () => addRangeRow());
$("#cancel-edit").addEventListener("click", closeEditor);

editor.addEventListener("submit", async (event) => {
  event.preventDefault();
  const name = $("#building-name").value.trim();
  const existing = editingIndex === null ? null : config.buildings[editingIndex];
  const building = { name, ranges: readRanges(), notes: existing?.notes || "" };
  const before = config.buildings;
  config.buildings = editingIndex === null
    ? config.buildings.concat(building)
    : config.buildings.map((item, index) => index === editingIndex ? building : item);
  try {
    await invoke("save_config", { config });
    closeEditor();
    await loadBuildings();
    setConfigStatus(`${name} saved.`);
  } catch (error) {
    config.buildings = before;
    editorStatus.textContent = "Couldn't save: " + error;
    editorStatus.classList.add("error");
  }
});

$("#delete-building").addEventListener("click", async () => {
  const building = config.buildings[editingIndex];
  if (!building || !window.confirm(`Delete ${building.name}? This cannot be undone.`)) return;
  const before = config.buildings;
  config.buildings = config.buildings.filter((_, index) => index !== editingIndex);
  try {
    await invoke("save_config", { config });
    closeEditor();
    await loadBuildings();
    setConfigStatus(`${building.name} deleted.`);
  } catch (error) {
    config.buildings = before;
    editorStatus.textContent = "Couldn't delete: " + error;
    editorStatus.classList.add("error");
  }
});

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
      closeEditor();
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
