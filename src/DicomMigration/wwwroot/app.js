const $ = (sel) => document.querySelector(sel);

async function api(path, opts) {
  const res = await fetch("/api" + path, opts);
  if (!res.ok && res.status !== 409) {
    // Surface the server's {"error": "..."} message rather than the raw JSON wrapper.
    const txt = await res.text();
    let msg = txt;
    try { const j = JSON.parse(txt); if (j && j.error) msg = j.error; } catch { /* not JSON */ }
    throw new Error(msg);
  }
  return res.status === 204 ? null : res.json();
}

async function postPhase(phase) {
  try {
    const r = await api("/" + phase, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: phase === "inventory" ? JSON.stringify(inventoryFilter()) : "{}",
    });
    if (r && r.error) alert(r.error);
  } catch (e) { alert(e.message); }
  refreshStatus();
}

// Build the InventoryFilter from the date inputs. <input type="date"> gives YYYY-MM-DD;
// DICOM wants YYYYMMDD, and a range as "from-to". Either end alone is a valid open range.
function inventoryFilter() {
  const dcm = (v) => (v ? v.replaceAll("-", "") : "");
  const from = dcm($("#i-from").value);
  const to = dcm($("#i-to").value);

  let range = null;
  if (from && to) range = from + "-" + to;
  else if (from) range = from + "-";
  else if (to) range = "-" + to;

  return range ? { studyDateRange: range } : {};
}

function pct(p, t) { return t > 0 ? Math.round((p / t) * 100) : 0; }

async function refreshStatus() {
  let data;
  try { data = await api("/status"); } catch { return; }
  const s = data.state;

  $("#phase-label").textContent = s.phase + (s.isRunning ? " (running)" : "");
  $("#progress-text").textContent = s.total > 0 ? `${s.processed}/${s.total} — ok ${s.succeeded}, fail ${s.failed}` : (s.message || "");
  $("#progress-fill").style.width = pct(s.processed, s.total) + "%";
  $("#current-item").textContent = s.currentItem ? "current: " + s.currentItem : "";

  // Disable phase buttons while busy. Connections must not change mid-run either.
  ["btn-inventory", "btn-migrate", "btn-verify", "btn-retry", "c-save"].forEach((id) => { $("#" + id).disabled = data.busy; });

  renderSummary(data.counts);
  renderCounts(data.counts);
}

// Top-of-page headline counters derived from the per-status counts.
function renderSummary(counts) {
  const get = (k) => counts[k] || 0;
  const total = Object.values(counts).reduce((a, b) => a + b, 0);
  // "Moved" = C-MOVE completed; verified studies were moved and then passed verification.
  const moved = get("moved") + get("verified");
  // Anything that went wrong: the move failed, verification failed, or counts didn't match.
  const failed = get("move_failed") + get("verify_failed") + get("mismatch");

  $("#stat-total").textContent = total;
  $("#stat-moved").textContent = moved;
  $("#stat-failed").textContent = failed;
  $("#stat-moved-sub").textContent = total > 0 ? `${pct(moved, total)}% of inventory` : "";
}

function renderCounts(counts) {
  const order = ["inventoried", "move_pending", "moved", "move_failed", "verified", "mismatch", "verify_failed"];
  const el = $("#counts");
  el.innerHTML = "";
  for (const k of order) {
    const n = counts[k] || 0;
    const div = document.createElement("div");
    div.className = "count-chip";
    div.innerHTML = `<div class="n">${n}</div><div class="lbl">${k}</div>`;
    el.appendChild(div);
  }
}

async function refreshStudies() {
  const status = $("#status-filter").value;
  let rows;
  try {
    rows = await api("/studies?limit=200" + (status ? "&status=" + status : ""));
  } catch { return; } // transient failure during polling; keep the current table
  const tbody = $("#studies tbody");
  tbody.innerHTML = "";
  for (const r of rows) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${esc(r.patientName)}<br><span class="uid">${esc(r.patientId)}</span></td>
      <td>${esc(r.studyDate)}</td>
      <td>${esc(r.accessionNumber)}</td>
      <td>${esc(r.modalities)}</td>
      <td>${r.sourceInstanceCount ?? ""}</td>
      <td>${r.destInstanceCount ?? ""}</td>
      <td><span class="status ${r.status}">${r.status}</span></td>
      <td>${r.attempts}</td>
      <td>${esc(r.lastError)}</td>
      <td><button class="push-btn" data-uid="${esc(r.studyInstanceUid)}">Push</button></td>`;
    tbody.appendChild(tr);
  }
  // Rows are rebuilt on every poll, so wire the buttons here rather than once at load.
  tbody.querySelectorAll(".push-btn").forEach((b) =>
    b.addEventListener("click", () => pushStudy(b.dataset.uid, b)));
}

// C-MOVE a single study on demand. Shares the phase gate with the bulk phases, so this
// returns 409 while a migration is running rather than competing with it.
async function pushStudy(uid, btn) {
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "...";
  try {
    const r = await api("/move-study", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ studyInstanceUid: uid }),
    });
    if (r && r.error) alert(r.error);
  } catch (e) {
    alert(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
  refreshStatus();
  refreshStudies();
}

async function refreshEvents() {
  let events;
  try {
    events = await api("/events?limit=80");
  } catch { return; } // transient failure during polling
  const ul = $("#event-list");
  ul.innerHTML = "";
  for (const e of events) {
    const li = document.createElement("li");
    li.className = "lvl-" + e.level;
    const ts = (e.created_at || "").replace("T", " ").slice(0, 19);
    li.innerHTML = `<span class="ts">${ts}</span>${esc(e.message)}`;
    ul.appendChild(li);
  }
}

function esc(s) {
  if (s == null) return "";
  return String(s).replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
}

async function loadConfig() {
  try {
    const c = await api("/config");
    $("#config").innerHTML =
      `local AE: <b>${esc(c.local)}</b><br>` +
      `source: ${esc(c.source.aeTitle)} @ ${esc(c.source.host)}:${c.source.port}<br>` +
      `dest: ${esc(c.destination.aeTitle)} @ ${esc(c.destination.host)}:${c.destination.port} — verify: ${esc(c.verifyLevel)}`;
  } catch { /* ignore */ }
}

// Load current settings into the throttle + connection forms (once, so we don't clobber edits).
async function loadSettings() {
  try {
    const s = await api("/settings");
    $("#t-parallel").value = s.move.maxParallelMoves;
    $("#t-delay").value = s.move.delayBetweenMovesMs;
    $("#t-verify").value = s.verify.level;
    $("#t-chunk").value = s.inventory.chunkDays;
    $("#t-chunkdelay").value = s.inventory.delayBetweenChunksMs;

    $("#c-local-ae").value = s.localAeTitle;
    $("#c-src-ae").value = s.source.aeTitle;
    $("#c-src-host").value = s.source.host;
    $("#c-src-port").value = s.source.port;
    $("#c-dst-ae").value = s.destination.aeTitle;
    $("#c-dst-host").value = s.destination.host;
    $("#c-dst-port").value = s.destination.port;
  } catch { /* ignore */ }
}

async function saveConnections() {
  const body = {
    "LocalAeTitle": $("#c-local-ae").value.trim(),
    "Source.AeTitle": $("#c-src-ae").value.trim(),
    "Source.Host": $("#c-src-host").value.trim(),
    "Source.Port": String($("#c-src-port").value || ""),
    "Destination.AeTitle": $("#c-dst-ae").value.trim(),
    "Destination.Host": $("#c-dst-host").value.trim(),
    "Destination.Port": String($("#c-dst-port").value || ""),
  };
  try {
    await api("/settings", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    flashSaved("Saved", "#c-saved");
    loadConfig();
  } catch (e) {
    flashSaved("Error: " + e.message, "#c-saved");
  }
}

// C-ECHO the values currently in the form, so a connection can be checked before saving.
async function testConnection(target) {
  const p = target === "source" ? "src" : "dst";
  const btn = $("#c-" + p + "-test");
  const msg = $("#c-" + p + "-test-msg");

  const body = {
    target: target,
    aeTitle: $("#c-" + p + "-ae").value.trim(),
    host: $("#c-" + p + "-host").value.trim(),
    port: Number($("#c-" + p + "-port").value) || null,
  };

  btn.disabled = true;
  msg.className = "test-msg";
  msg.textContent = "Testing...";

  try {
    const r = await api("/test-connection", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    msg.className = "test-msg " + (r.ok ? "test-ok" : "test-err");
    msg.textContent = (r.ok ? "Success" : r.message) + " (" + r.elapsedMs + "ms)";
  } catch (e) {
    msg.className = "test-msg test-err";
    msg.textContent = e.message;
  } finally {
    btn.disabled = false;
  }
}

async function saveSettings() {
  const body = {
    "Move.MaxParallelMoves": String($("#t-parallel").value || "1"),
    "Move.DelayBetweenMovesMs": String($("#t-delay").value || "0"),
    "Verify.Level": $("#t-verify").value,
    "Inventory.ChunkDays": String($("#t-chunk").value || "0"),
    "Inventory.DelayBetweenChunksMs": String($("#t-chunkdelay").value || "0"),
  };
  try {
    await api("/settings", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    flashSaved("Saved", "#t-saved");
    loadConfig();
  } catch (e) {
    flashSaved("Error: " + e.message, "#t-saved");
  }
}

function flashSaved(msg, sel) {
  const el = $(sel || "#t-saved");
  el.textContent = msg;
  setTimeout(() => { el.textContent = ""; }, 3000);
}

// Wire up controls.
document.querySelectorAll(".phase-bar button[data-phase]").forEach((b) =>
  b.addEventListener("click", () => postPhase(b.dataset.phase)));
$("#btn-cancel").addEventListener("click", () => api("/cancel", { method: "POST" }));
$("#refresh").addEventListener("click", () => { refreshStudies(); refreshEvents(); });
$("#status-filter").addEventListener("change", refreshStudies);
$("#t-save").addEventListener("click", saveSettings);
$("#c-save").addEventListener("click", saveConnections);
$("#c-src-test").addEventListener("click", () => testConnection("source"));
$("#c-dst-test").addEventListener("click", () => testConnection("destination"));

// Initial load + polling.
loadConfig();
loadSettings();
refreshStatus();
refreshStudies();
refreshEvents();
setInterval(refreshStatus, 1500);
setInterval(() => { refreshStudies(); refreshEvents(); }, 5000);
