const $ = (sel) => document.querySelector(sel);

async function api(path, opts) {
  // X-Requested-With is the server's CSRF guard on POSTs: a cross-origin page cannot set a
  // custom header without a preflight, and this API sends no CORS headers, so the preflight fails.
  const o = { ...(opts || {}) };
  o.headers = { ...(o.headers || {}), "X-Requested-With": "XMLHttpRequest" };
  const res = await fetch("/api" + path, o);
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

// Banner shown when polling fails, so "the service is down" never reads as "still running".
function setConnected(ok, detail) {
  const el = $("#conn-banner");
  if (!el) return;
  if (ok) {
    el.hidden = true;
    el.textContent = "";
  } else {
    el.hidden = false;
    el.textContent = "Lost contact with the server - displayed state is stale. " + (detail || "");
  }
}

// Latest per-status counts, kept so the table can report "showing X of Y" without another call.
let lastCounts = {};
let isBusy = false;

async function refreshStatus() {
  let data;
  try {
    data = await api("/status");
    setConnected(true);
  } catch (e) {
    // Do NOT fail silently: a dead backend used to leave the last-known state on screen, so a
    // stopped service was indistinguishable from a migration still running.
    setConnected(false, e.message);
    return;
  }
  const s = data.state;
  lastCounts = data.counts || {};
  isBusy = !!data.busy;

  $("#phase-label").textContent = s.phase + (s.isRunning ? " (running)" : "");
  $("#progress-text").textContent = s.total > 0 ? `${s.processed}/${s.total} — ok ${s.succeeded}, fail ${s.failed}` : "";
  $("#progress-fill").style.width = pct(s.processed, s.total) + "%";
  $("#current-item").textContent = s.currentItem ? "current: " + s.currentItem : "";

  // Always render the phase message. It previously shared a slot with the progress counter and
  // only appeared when total was 0 -- but every phase sets Total before it can fail, so a phase
  // that aborted mid-run showed nothing at all.
  const msg = $("#phase-message");
  if (s.message) {
    msg.hidden = false;
    msg.textContent = s.message;
    msg.className = "phase-message" + (/^error/i.test(s.message) ? " is-error"
      : /cancel/i.test(s.message) ? " is-warn" : "");
  } else {
    msg.hidden = true;
    msg.textContent = "";
  }

  // Disable phase buttons while busy. Connections must not change mid-run either. Per-row Push
  // and Verify are included: they share the phase gate, so clicking one mid-run just 409s.
  ["btn-inventory", "btn-migrate", "btn-verify", "btn-retry"].forEach((id) => { $("#" + id).disabled = data.busy; });
  // Save stays disabled while busy OR until settings have actually loaded -- this poll runs every
  // 1.5s and would otherwise re-enable it and undo the failed-load guard.
  $("#c-save").disabled = data.busy || !settingsLoaded;
  $("#t-save").disabled = data.busy || !settingsLoaded;
  document.querySelectorAll(".push-btn, .verify-btn").forEach((b) => { b.disabled = data.busy; });

  // Cancel is the inverse: only meaningful while something is running.
  $("#btn-cancel").disabled = !data.busy;

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

const PAGE_SIZE = 200;
let pageOffset = 0;
let studiesSeq = 0;      // newest-wins guard against out-of-order responses
let studiesInFlight = false;
let lastStudiesKey = null;

// Total rows matching the current filter, derived from the status counts we already poll.
function filteredTotal(status) {
  if (status) return lastCounts[status] || 0;
  return Object.values(lastCounts).reduce((a, b) => a + b, 0);
}

async function refreshStudies() {
  // Skip a scheduled poll while one is still outstanding, and drop any response that is not the
  // newest: refreshStudies is driven by the interval, the filter's change handler, the Refresh
  // button and the paging buttons, so a slow earlier response could otherwise land last and
  // repaint the table with rows that contradict the current filter.
  if (studiesInFlight) return;
  const seq = ++studiesSeq;
  const status = $("#status-filter").value;

  let rows;
  studiesInFlight = true;
  try {
    rows = await api(`/studies?limit=${PAGE_SIZE}&offset=${pageOffset}` + (status ? "&status=" + status : ""));
  } catch {
    return; // transient failure during polling; keep the current table
  } finally {
    studiesInFlight = false;
  }
  if (seq !== studiesSeq) return;
  if (status !== $("#status-filter").value) return; // filter changed while in flight

  renderPaging(status, rows.length);

  // Skip the rebuild when nothing changed. The table is repainted every 5s, and clearing innerHTML
  // destroys focus, text selection (an operator copying a UID) and any in-progress interaction.
  const key = JSON.stringify(rows) + "|" + pageOffset + "|" + status;
  if (key === lastStudiesKey) return;
  lastStudiesKey = key;

  const tbody = $("#studies tbody");
  const activeId = document.activeElement && document.activeElement.dataset
    ? document.activeElement.dataset.rowkey : null;

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
      <td><span class="status ${esc(r.status)}">${esc(r.status)}</span></td>
      <td>${r.attempts}</td>
      <td>${esc(r.lastError)}</td>
      <td class="row-actions"></td>`;

    // Build the action buttons through the DOM rather than string interpolation, and close over
    // the UID instead of storing it in an attribute. PACS-supplied values never reach the parser
    // this way, so this stays safe regardless of what esc() does.
    const actions = tr.querySelector(".row-actions");
    actions.appendChild(actionButton("Push", "push-btn", "/move-study", r));
    actions.appendChild(actionButton("Verify", "verify-btn", "/verify-study", r));

    tbody.appendChild(tr);
  }

  // Restore focus if the row the operator was on still exists after the repaint.
  if (activeId) {
    const restore = tbody.querySelector(`[data-rowkey="${CSS.escape(activeId)}"]`);
    if (restore) restore.focus();
  }
}

function renderPaging(status, shown) {
  const total = filteredTotal(status);
  const first = total === 0 ? 0 : pageOffset + 1;
  const last = pageOffset + shown;

  $("#page-info").textContent = total > 0
    ? `showing ${first}-${last} of ${total}`
    : `showing ${shown}`;

  $("#page-prev").disabled = pageOffset <= 0;
  $("#page-next").disabled = shown < PAGE_SIZE || last >= total;
}

function actionButton(label, cls, path, r) {
  const b = document.createElement("button");
  b.className = cls;
  b.textContent = label;
  b.disabled = isBusy;
  // Stable key for focus restoration across the 5s repaint.
  b.dataset.rowkey = cls + ":" + r.studyInstanceUid;
  // A study that already verified rarely needs pushing again; demote rather than hide, since
  // re-pushing is occasionally the right call.
  if (cls === "push-btn" && r.status === "verified") b.classList.add("demoted");
  b.addEventListener("click", () => studyAction(path, r, b));
  return b;
}

// Per-study push (C-MOVE) or re-verify (read-only C-FIND). Both share the phase gate with the
// bulk phases, so they return 409 while a migration is running rather than competing with it.
async function studyAction(path, r, btn) {
  // Push moves patient data to the destination PACS, and the table re-sorts by updated_at every
  // 5s so rows shift under the cursor. Naming the study in the prompt means a mis-click is
  // caught before anything transfers. Verify is read-only, so it needs no confirmation.
  if (path === "/move-study") {
    const who = [r.patientName, r.patientId].filter(Boolean).join(" / ") || "(unknown patient)";
    const what = [r.studyDate, r.modalities, r.studyDescription].filter(Boolean).join(" ");
    if (!confirm(`Push this study to the destination PACS?\n\n${who}\n${what}\nAccession: ${r.accessionNumber || "-"}\nCurrent status: ${r.status}`)) {
      return;
    }
  }

  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "...";
  try {
    const res = await api(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ studyInstanceUid: r.studyInstanceUid }),
    });
    if (res && res.error) alert(res.error);
  } catch (e) {
    alert(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
  refreshStatus();
  lastStudiesKey = null; // force a repaint so the new status shows immediately
  refreshStudies();
}

let lastEventsKey = null;

async function refreshEvents() {
  let events;
  try {
    events = await api("/events?limit=80");
  } catch { return; } // transient failure during polling

  // Same no-op guard as the study table: rebuilding this list every 5s discarded the operator's
  // scroll position, so reading back through the log was impossible while a phase was running.
  const key = JSON.stringify(events);
  if (key === lastEventsKey) return;
  lastEventsKey = key;

  const ul = $("#event-list");
  const prevScroll = ul.scrollTop;
  const wasAtTop = prevScroll <= 2;

  ul.innerHTML = "";
  for (const e of events) {
    const li = document.createElement("li");
    li.className = "lvl-" + e.level;
    const ts = (e.created_at || "").replace("T", " ").slice(0, 19);
    li.innerHTML = `<span class="ts">${ts}</span>${esc(e.message)}`;
    ul.appendChild(li);
  }

  // Keep the reader where they were. If they were at the top (newest first), stay pinned there
  // so new entries remain visible.
  ul.scrollTop = wasAtTop ? 0 : prevScroll;
}

// Escapes for BOTH text and attribute context. Quotes matter: study metadata (patient name,
// UIDs, descriptions) comes from the source PACS, so a value containing a double quote could
// otherwise close an attribute and inject event handlers into the dashboard's own origin.
function esc(s) {
  if (s == null) return "";
  return String(s).replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
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

// True once the forms hold real server values. Save is blocked until then: the forms otherwise
// sit at their HTML defaults (empty numbers, and Verify.Level at its first option, "Study"), and
// Save posts every field -- so saving after a failed load silently downgraded verification depth
// and reset the throttle to fallback values the operator never chose.
let settingsLoaded = false;

function setSettingsAvailable(ok, detail) {
  settingsLoaded = ok;
  $("#t-save").disabled = !ok;
  $("#c-save").disabled = !ok;
  if (!ok) {
    flashSaved("Settings failed to load - Save disabled. " + (detail || ""), "#t-saved", 0);
  }
}

// Load current settings into the throttle + connection forms (once, so we don't clobber edits).
async function loadSettings() {
  try {
    const s = await api("/settings");
    // Clamp to the input's range. A value seeded outside it (the installers write the settings
    // table directly, bypassing API validation) would otherwise load into the form and make Save
    // fail on every attempt, since Save re-posts every field.
    $("#t-parallel").value = Math.min(Math.max(s.move.maxParallelMoves, 1), 16);
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
    setSettingsAvailable(true);
  } catch (e) {
    setSettingsAvailable(false, e.message);
  }
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
    // Re-read so the form shows what the server actually stored, not what was typed. Save posts
    // every field, so without this the form stays a stale snapshot that would overwrite any
    // change made elsewhere on the next Save.
    loadSettings();
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
    loadSettings();
  } catch (e) {
    flashSaved("Error: " + e.message, "#t-saved");
  }
}

// ms = 0 keeps the message up (used for states the operator must not miss, like a failed load).
// Cancel aborts a running phase. Previously it fired and forgot: the promise was never awaited
// and had no catch, so a failure surfaced only as an unhandled rejection in devtools.
async function cancelPhase() {
  if (!confirm("Cancel the running phase?\n\nStudies already transferred are unaffected, but the phase will stop where it is.")) {
    return;
  }
  const btn = $("#btn-cancel");
  btn.disabled = true;
  try {
    await api("/cancel", { method: "POST" });
  } catch (e) {
    alert("Cancel failed: " + e.message);
  }
  refreshStatus();
}

function flashSaved(msg, sel, ms) {
  const el = $(sel || "#t-saved");
  el.textContent = msg;
  if (ms !== 0) setTimeout(() => { el.textContent = ""; }, ms || 3000);
}

// Wire up controls.
document.querySelectorAll(".phase-bar button[data-phase]").forEach((b) =>
  b.addEventListener("click", () => postPhase(b.dataset.phase)));
$("#btn-cancel").addEventListener("click", cancelPhase);
$("#refresh").addEventListener("click", () => { lastStudiesKey = null; refreshStudies(); refreshEvents(); });
// Reset to the first page when the filter changes, or the view can land past the end of a
// smaller result set and show nothing.
$("#status-filter").addEventListener("change", () => { pageOffset = 0; refreshStudies(); });
$("#page-prev").addEventListener("click", () => {
  pageOffset = Math.max(0, pageOffset - PAGE_SIZE);
  refreshStudies();
});
$("#page-next").addEventListener("click", () => {
  pageOffset += PAGE_SIZE;
  refreshStudies();
});
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
