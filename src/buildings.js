// Building Generator window. Edits buildings.json via the shared Tauri commands.
const { invoke } = window.__TAURI__.core;

let config = { buildings: [] };
let editingIndex = null;

const $ = (s) => document.querySelector(s);
const val = (s) => $(s).value;
const escapeHtml = (s) =>
  String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

// ---- CIDR math for the live range display ----
const intToIp = (n) => [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255].join(".");
function isPrivate(n) {
  const a = (n >>> 24) & 255;
  const b = (n >>> 16) & 255;
  return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
}
function describeCidr(text) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:\/(\d{1,2}))?$/.exec(text.trim());
  if (!m) return null;
  const octets = [+m[1], +m[2], +m[3], +m[4]];
  if (octets.some((x) => x > 255)) return null;
  const prefix = m[5] === undefined ? 32 : +m[5];
  if (prefix > 32) return null;
  const ipInt = ((octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]) >>> 0;
  const mask = prefix === 0 ? 0 : (0xffffffff << (32 - prefix)) >>> 0;
  const network = (ipInt & mask) >>> 0;
  const total = Math.pow(2, 32 - prefix);
  const broadcast = (network + total - 1) >>> 0;
  const hosts = prefix >= 31 ? total : total - 2;
  return {
    network: intToIp(network),
    broadcast: intToIp(broadcast),
    total,
    hosts,
    private: isPrivate(network) && isPrivate(broadcast),
  };
}

// ---- Range editor rows ----
function updateRangeInfo(cidrInput, info) {
  const text = cidrInput.value.trim();
  if (!text) {
    info.textContent = "";
    info.className = "range-info";
    return;
  }
  const d = describeCidr(text);
  if (!d) {
    info.textContent = "invalid";
    info.className = "range-info error";
    return;
  }
  const span = `${d.network} – ${d.broadcast} · ${d.total.toLocaleString()} addresses (${d.hosts.toLocaleString()} scannable)`;
  if (!d.private) {
    info.textContent = `${span} — not private, won't scan`;
    info.className = "range-info error";
  } else {
    info.textContent = span;
    info.className = "range-info";
  }
}

function addRangeRow(name = "", cidr = "") {
  const row = document.createElement("div");
  row.className = "range-row";

  const nameInput = document.createElement("input");
  nameInput.type = "text";
  nameInput.className = "range-name";
  nameInput.placeholder = "First Floor";
  nameInput.value = name;

  const cidrInput = document.createElement("input");
  cidrInput.type = "text";
  cidrInput.className = "range-cidr";
  cidrInput.placeholder = "192.168.10.0/24";
  cidrInput.value = cidr;

  const info = document.createElement("span");
  info.className = "range-info";

  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "link-danger";
  remove.textContent = "×";
  remove.addEventListener("click", () => row.remove());

  cidrInput.addEventListener("input", () => updateRangeInfo(cidrInput, info));
  updateRangeInfo(cidrInput, info);

  row.append(nameInput, cidrInput, info, remove);
  $("#b-ranges-list").appendChild(row);
}

const readRanges = () =>
  Array.from(document.querySelectorAll("#b-ranges-list .range-row"))
    .map((row) => ({
      name: row.querySelector(".range-name").value.trim(),
      cidr: row.querySelector(".range-cidr").value.trim(),
    }))
    .filter((r) => r.cidr);

function setRanges(ranges) {
  $("#b-ranges-list").replaceChildren();
  const list = ranges && ranges.length ? ranges : [{ name: "", cidr: "" }];
  list.forEach((r) => addRangeRow(r.name || "", r.cidr || ""));
}

$("#add-range").addEventListener("click", () => addRangeRow());

// ---- Buildings table ----
const rangesLabel = (b) =>
  b.ranges && b.ranges.length ? b.ranges.map((r) => r.name || r.cidr).join(", ") : "—";

function renderBuildings() {
  const tbody = $("#buildings-table tbody");
  tbody.replaceChildren();
  config.buildings.forEach((b, i) => {
    const tr = document.createElement("tr");
    tr.innerHTML =
      `<td>${escapeHtml(b.name)}</td>` +
      `<td class="ranges-cell">${escapeHtml(rangesLabel(b))}</td>`;
    const actions = document.createElement("td");
    const edit = document.createElement("button");
    edit.textContent = "Edit";
    edit.className = "link";
    edit.addEventListener("click", () => startEdit(i));
    actions.appendChild(edit);
    const del = document.createElement("button");
    del.textContent = "Delete";
    del.className = "link-danger";
    del.addEventListener("click", () => removeBuilding(i));
    actions.appendChild(del);
    tr.appendChild(actions);
    tbody.appendChild(tr);
  });
}

async function loadConfig() {
  try {
    config = await invoke("get_config");
    renderBuildings();
  } catch (e) {
    console.error("get_config failed", e);
  }
}

const saveConfig = () => invoke("save_config", { config });

async function removeBuilding(i) {
  const before = config.buildings;
  config.buildings = config.buildings.filter((_, idx) => idx !== i);
  try {
    await saveConfig();
    if (editingIndex === i) resetForm();
    else if (editingIndex !== null && editingIndex > i) editingIndex -= 1;
    renderBuildings();
  } catch (e) {
    config.buildings = before;
    alert("Couldn't save: " + e);
  }
}

// ---- Add / edit form ----
const formTitle = $("#b-form-title");
const submitBtn = $("#b-submit");
const cancelBtn = $("#b-cancel");

function resetForm() {
  editingIndex = null;
  $("#b-name").value = "";
  setRanges([]);
  formTitle.textContent = "Add a building";
  submitBtn.textContent = "Add";
  cancelBtn.hidden = true;
}
function clearBStatus() {
  const s = $("#b-status");
  s.textContent = "";
  s.classList.remove("error");
}
function startEdit(i) {
  const b = config.buildings[i];
  if (!b) return;
  editingIndex = i;
  $("#b-name").value = b.name;
  setRanges(b.ranges || []);
  formTitle.textContent = `Edit ${b.name}`;
  submitBtn.textContent = "Save changes";
  cancelBtn.hidden = false;
  clearBStatus();
  $("#add-building").scrollIntoView({ behavior: "smooth", block: "start" });
}
cancelBtn.addEventListener("click", () => {
  resetForm();
  clearBStatus();
});

$("#add-building").addEventListener("submit", async (e) => {
  e.preventDefault();
  const status = $("#b-status");
  const name = val("#b-name").trim();
  if (!name) {
    status.textContent = "Name is required.";
    status.classList.add("error");
    return;
  }

  const editing = editingIndex !== null;
  const existing = editing ? config.buildings[editingIndex] : null;
  const building = { name, ranges: readRanges(), notes: existing ? existing.notes : "" };

  const before = config.buildings;
  config.buildings = editing
    ? config.buildings.map((b, idx) => (idx === editingIndex ? building : b))
    : config.buildings.concat(building);

  try {
    await saveConfig();
    renderBuildings();
    resetForm();
    status.textContent = editing ? `Updated ${name}.` : `Added ${name}.`;
    status.classList.remove("error");
  } catch (err) {
    config.buildings = before;
    status.textContent = "Couldn't save: " + err;
    status.classList.add("error");
  }
});

// ---- Import / Export the whole config ----
const importBtn = $("#import-btn");
const importFile = $("#import-file");
const exportBtn = $("#export-btn");
const ioStatus = $("#io-status");
const setIo = (msg, isError = false) => {
  ioStatus.textContent = msg;
  ioStatus.classList.toggle("error", isError);
};

importBtn.addEventListener("click", () => importFile.click());
importFile.addEventListener("change", () => {
  const file = importFile.files[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = async () => {
    try {
      const parsed = JSON.parse(reader.result);
      if (!parsed || !Array.isArray(parsed.buildings)) {
        throw new Error("not a Surveil config (needs a buildings array)");
      }
      await invoke("save_config", { config: parsed });
      config = await invoke("get_config");
      renderBuildings();
      setIo(`Imported ${parsed.buildings.length} buildings from ${file.name}.`);
    } catch (e) {
      setIo("Import failed: " + e, true);
    } finally {
      importFile.value = "";
    }
  };
  reader.onerror = () => setIo("Couldn't read the file.", true);
  reader.readAsText(file);
});

exportBtn.addEventListener("click", async () => {
  try {
    const path = await invoke("export_config");
    setIo("Exported to " + path);
  } catch (e) {
    setIo("Export failed: " + e, true);
  }
});

// ---- Jump straight to editing a building when opened from the Scanner ----
function startEditByName(name) {
  const i = config.buildings.findIndex((b) => b.name === name);
  if (i >= 0) startEdit(i);
}

// Already-open window: react to a new edit request.
window.__TAURI__.event.listen("edit-building", (e) => startEditByName(e.payload));

resetForm();
(async () => {
  await loadConfig();
  // Freshly-opened window: pick up the pending edit request.
  const target = await invoke("take_pending_edit");
  if (target) startEditByName(target);
})();
