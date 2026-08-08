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

// ---- Formatting helpers: phrasing rules live here, not scattered through the render code ----

function fmtDuration(ms) {
  if (!isFinite(ms) || ms < 0) return "-";
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  if (h > 0) return `${h}h ${String(m).padStart(2, "0")}m`;
  if (m > 0) return `${m}m ${String(sec).padStart(2, "0")}s`;
  return `${sec}s`;
}

// Separators on large counts only: five-digit unseparated numbers are misread at a glance,
// but "1,285 studies" reads worse than "1285" for small values.
function fmtCount(n) { return n >= 1000 ? n.toLocaleString("en-US") : String(n); }

const PHASE_VERB = {
  idle: "Idle",
  inventory: "Inventorying source",
  migrate: "Migrating",
  retry: "Retrying failures",
  verify: "Verifying",
  "move-study": "Pushing one study",
  "verify-study": "Verifying one study",
};

// Rolling window of completion samples, used for throughput and ETA. A whole-run average is
// misleading after a slow patch; a short window tracks what is happening now.
let rateSamples = [];
let phaseKey = null;

function updateRate(state) {
  const key = state.phase + "|" + (state.startedAt || "");
  if (key !== phaseKey) { phaseKey = key; rateSamples = []; }
  if (!state.isRunning) return;

  const now = Date.now();
  const last = rateSamples[rateSamples.length - 1];
  if (!last || last.processed !== state.processed) {
    rateSamples.push({ t: now, processed: state.processed });
  }
  // Keep ~2 minutes of samples.
  while (rateSamples.length > 2 && now - rateSamples[0].t > 120000) rateSamples.shift();
}

function ratePerMin() {
  if (rateSamples.length < 2) return null;
  const a = rateSamples[0], b = rateSamples[rateSamples.length - 1];
  const mins = (b.t - a.t) / 60000;
  const done = b.processed - a.processed;
  if (mins <= 0 || done <= 0) return null;
  return done / mins;
}

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
let lastRev = -1;
let lastState = null;

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
  isBusy = !!data.busy;
  lastState = s;

  // Skip the whole render when the server state has not advanced. This is what makes a
  // sub-second poll cheap, and it keeps text selection alive between ticks.
  if (s.rev === lastRev && !s.isRunning) return;
  lastRev = s.rev;

  updateRate(s);
  renderActivity(s);

  const label = $("#phase-label");
  label.textContent = (PHASE_VERB[s.phase] || s.phase) + (s.isRunning ? "…" : "");
  label.title = s.phase; // raw slug kept for support
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

  // Cancel and Pause are the inverse: only meaningful while something is running.
  $("#btn-cancel").disabled = !data.busy;
  const pause = $("#btn-pause");
  pause.disabled = !data.busy;
  pause.textContent = s.paused ? "Resume" : "Pause";
  pause.classList.toggle("is-paused", !!s.paused);

}

// The live activity block: one row per in-flight DICOM operation, plus the derived strip.
function renderActivity(s) {
  const rows = $("#activity-rows");
  const panel = $("#activity");

  panel.classList.toggle("is-running", !!s.isRunning);
  panel.classList.toggle("is-failed", !s.isRunning && s.failed > 0);

  // Inventory chunking: the query phase has no per-study granularity, so show the window.
  const chunkLine = s.chunkCount > 1
    ? `<div class="act-row"><span class="act-op">C-FIND</span> window ${s.chunkIndex} of ${s.chunkCount} <span class="act-mono">${esc(s.chunkRange || "")}</span></div>`
    : "";

  const opRows = (s.inFlight || []).map((o) => {
    const who = o.label ? esc(o.label) : esc(o.studyInstanceUid);
    // Sub-operation counters only exist for C-MOVE, and only once the peer reports them.
    const sub = o.subOpsCompleted > 0 || o.subOpsRemaining > 0
      ? ` — ${fmtCount(o.subOpsCompleted)}${o.subOpsRemaining >= 0 ? " of " + fmtCount(o.subOpsCompleted + o.subOpsRemaining) : ""} instances`
        + (o.subOpsFailed > 0 ? `, <span class="act-fail">${o.subOpsFailed} failed</span>` : "")
      : "";
    const stalled = isStalled(o) ? ` <span class="act-stall">no progress for ${fmtDuration(Date.now() - Date.parse(o.lastProgressAtUtc || o.startedAtUtc))}</span>` : "";
    return `<div class="act-row">
      <span class="act-op">${esc(o.operation)}</span>
      <span class="act-arrow">→</span>
      <span class="act-mono">${esc(o.peerAe)}@${esc(o.peerHost)}:${o.peerPort}</span>
      <span class="act-who">${who}</span>${sub}${stalled}
    </div>`;
  }).join("");

  rows.innerHTML = chunkLine + opRows;
  rows.hidden = !(chunkLine || opRows);

  // Derived strip.
  const bits = [];
  if (s.startedAt) {
    const end = s.isRunning ? Date.now() : (s.finishedAt ? Date.parse(s.finishedAt) : Date.now());
    bits.push("elapsed " + fmtDuration(end - Date.parse(s.startedAt)));
  }
  const rate = ratePerMin();
  if (rate) {
    bits.push(rate.toFixed(1) + "/min");
    const left = s.total - s.processed;
    if (s.isRunning && left > 0) {
      bits.push("~" + fmtDuration((left / rate) * 60000) + " left");
    }
  }
  if (s.instancesTransferred > 0) bits.push(fmtCount(s.instancesTransferred) + " instances");

  const derived = $("#activity-derived");
  derived.textContent = bits.join(" · ");
  derived.hidden = bits.length === 0;
}

// A hung association looks identical to a slow one -- the counters just stop. Anything past this
// with no movement is worth flagging rather than leaving the operator to guess.
const STALL_MS = 45000;
function isStalled(o) {
  const t = Date.parse(o.lastProgressAtUtc || o.startedAtUtc);
  return isFinite(t) && Date.now() - t > STALL_MS;
}

// Completion estimate. Deliberately shows the measurements behind the number, not just an ETA:
// a bare "12 days" with no visible basis invites either false confidence or dismissal.
async function refreshEstimate() {
  let e;
  try { e = await api("/estimate"); } catch { return; }

  const sec = $("#estimate");
  if (!e || e.pendingStudies === 0) { sec.hidden = true; return; }
  sec.hidden = false;

  const cells = [];
  cells.push(["Remaining", `${fmtCount(e.pendingStudies)} studies`]);
  cells.push(["", `${fmtCount(e.projectedPendingInstances)} instances`]);

  if (e.instancesPerSecond) {
    cells.push(["Throughput", `${e.instancesPerSecond.toFixed(1)} instances/sec`]);
    cells.push(["Time left", fmtLongDuration(e.secondsRemaining * 1000)]);
    const done = new Date(Date.now() + e.secondsRemaining * 1000);
    cells.push(["Est. finish", done.toLocaleString()]);
  } else {
    cells.push(["Throughput", "not measured yet"]);
    cells.push(["Time left", "run a migration to measure"]);
  }

  $("#estimate-grid").innerHTML = cells
    .map(([k, v]) => `<div class="est-cell"><div class="est-k">${esc(k)}</div><div class="est-v">${esc(v)}</div></div>`)
    .join("");

  // State the basis, including the parts that make the number a floor rather than a promise.
  const notes = [];
  if (e.movedStudies > 0) {
    notes.push(`based on ${fmtCount(e.movedStudies)} timed move${e.movedStudies === 1 ? "" : "s"} ` +
               `(${fmtCount(e.movedInstances)} instances in ${fmtLongDuration(e.movedMs)}) — ${e.confidence}`);
  }
  if (e.pendingUnknownInstances > 0) {
    notes.push(`${fmtCount(e.pendingUnknownInstances)} studies have no instance count from the source; ` +
               `priced at the observed average of ${(e.averageInstancesPerStudy || 0).toFixed(0)}`);
  }
  notes.push("assumes continuous transfer at the current parallelism");
  $("#estimate-note").textContent = notes.join(" · ");
}

// Days matter here: a multi-day migration rendered as "287h 14m" is unreadable.
function fmtLongDuration(ms) {
  if (!isFinite(ms) || ms < 0) return "-";
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return fmtDuration(ms);
}

// Counts come from the slow lane now: /api/status is deliberately database-free so it can be
// polled sub-second without dragging a GROUP BY over the studies table along with it.
async function refreshCounts() {
  try {
    lastCounts = await api("/counts");
  } catch { return; }
  renderSummary(lastCounts);
  renderCounts(lastCounts);
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
    $("#t-acceptpartial").checked = !!s.move.acceptMissingNonImageObjects;
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
    "Move.AcceptMissingNonImageObjects": String($("#t-acceptpartial").checked),
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
// Pause holds before the NEXT study starts; studies already transferring run to completion.
// No confirmation: it is non-destructive and instantly reversible, unlike Cancel.
async function togglePause() {
  const btn = $("#btn-pause");
  const resuming = btn.textContent === "Resume";
  btn.disabled = true;
  try {
    await api(resuming ? "/resume" : "/pause", { method: "POST" });
  } catch (e) {
    alert((resuming ? "Resume" : "Pause") + " failed: " + e.message);
  }
  refreshStatus();
}

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
$("#btn-pause").addEventListener("click", togglePause);
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
refreshCounts();
refreshEstimate();
refreshStudies();
refreshEvents();

// Fast lane: memory-only state, so this can run sub-second and the activity block reads as live.
setInterval(refreshStatus, 600);
// Slow lane: everything that touches SQLite.
setInterval(() => { refreshCounts(); refreshStudies(); refreshEvents(); }, 5000);
// The estimate only moves when a study finishes, so it does not need the 5s lane.
setInterval(refreshEstimate, 15000);
// Elapsed/ETA tick independently of the poll so the clock does not appear to stutter.
setInterval(() => { if (isBusy && lastState) renderActivity(lastState); }, 1000);
