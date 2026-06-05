"use strict";

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

const ICONS = { Pass: "✅", Fail: "❌", Review: "⚠️", NotChecked: "➖" };
const VERDICT = {
  Pass:   { cls: "pass",   icon: "✅", title: "Looks good",        msg: "Everything checked matches and the warning is correct." },
  Fail:   { cls: "fail",   icon: "❌", title: "Needs attention",   msg: "One or more checks did not pass. See the details below." },
  Review: { cls: "review", icon: "⚠️", title: "Please take a look", msg: "Mostly fine, but something needs a human glance." },
};

/* ---------------- Engine state ---------------- */
let HEALTH = null;

function firewallOn() { return $("#firewallToggle")?.checked === true; }

/** Adds engine=offline to a FormData when the firewall switch is on. */
function applyEngine(fd) { if (firewallOn()) fd.set("engine", "offline"); return fd; }

function updateBadge() {
  const badge = $("#engineBadge");
  if (!badge) return;
  if (firewallOn()) {
    badge.textContent = "🔒 Firewall mode · " + (HEALTH?.fallbackEngine || "offline OCR");
    badge.classList.add("offline");
  } else if (HEALTH?.primaryAvailable) {
    badge.textContent = "● Reading with " + HEALTH.primaryEngine;
    badge.classList.remove("offline");
  } else if (HEALTH?.fallbackAvailable) {
    badge.textContent = "● Offline mode · " + HEALTH.fallbackEngine;
    badge.classList.add("offline");
  } else {
    badge.textContent = HEALTH ? "No reader available" : "";
  }
}

async function loadHealth() {
  try {
    HEALTH = await (await fetch("/api/health")).json();
  } catch {
    HEALTH = null;
  }
  updateBadge();
}

function setupFirewallToggle() {
  $("#firewallToggle")?.addEventListener("change", updateBadge);
}

/* ---------------- Tabs ---------------- */
function setupTabs() {
  const single = $("#tabSingle"), batch = $("#tabBatch");
  const sp = $("#singlePanel"), bp = $("#batchPanel");
  function show(which) {
    const isSingle = which === "single";
    single.classList.toggle("is-active", isSingle);
    batch.classList.toggle("is-active", !isSingle);
    single.setAttribute("aria-selected", isSingle);
    batch.setAttribute("aria-selected", !isSingle);
    sp.hidden = !isSingle;
    bp.hidden = isSingle;
  }
  single.addEventListener("click", () => show("single"));
  batch.addEventListener("click", () => show("batch"));
}

/* ---------------- Drag & drop helper ---------------- */
function wireDrop(zone, onFiles) {
  ["dragenter", "dragover"].forEach(e =>
    zone.addEventListener(e, ev => { ev.preventDefault(); zone.classList.add("dragover"); }));
  ["dragleave", "drop"].forEach(e =>
    zone.addEventListener(e, ev => { ev.preventDefault(); zone.classList.remove("dragover"); }));
  zone.addEventListener("drop", ev => { if (ev.dataTransfer?.files?.length) onFiles(ev.dataTransfer.files); });
  zone.addEventListener("keydown", ev => {
    if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); $("input[type=file]", zone)?.click(); }
  });
}

/* ---------------- Single mode ---------------- */
function setupSingle() {
  const input = $("#singleFile");
  const zone = $("#dropZone");
  const preview = $("#preview");
  const prompt = $("#dropPrompt");
  const clearBtn = $("#clearImage");
  const verifyBtn = $("#verifyBtn");
  let file = null;

  function setFile(f) {
    if (!f || !f.type.startsWith("image/")) return;
    file = f;
    preview.src = URL.createObjectURL(f);
    preview.hidden = false;
    prompt.hidden = true;
    clearBtn.hidden = false;
    verifyBtn.disabled = false;
  }
  function clear() {
    file = null; input.value = "";
    preview.hidden = true; prompt.hidden = false; clearBtn.hidden = true; verifyBtn.disabled = true;
  }

  input.addEventListener("change", () => input.files[0] && setFile(input.files[0]));
  wireDrop(zone, files => { input.files = files; setFile(files[0]); });
  clearBtn.addEventListener("click", e => { e.preventDefault(); clear(); });

  verifyBtn.addEventListener("click", async () => {
    if (!file) return;
    const out = $("#singleResult");
    out.innerHTML = spinner("Reading the label and checking it…");
    verifyBtn.disabled = true;

    const fd = new FormData($("#appForm"));
    fd.append("image", file);
    applyEngine(fd);
    try {
      const r = await fetch("/api/verify", { method: "POST", body: fd });
      const data = await r.json();
      if (!r.ok) { out.innerHTML = errorBox(data.error || "Something went wrong."); return; }
      out.innerHTML = "";
      out.appendChild(renderResult(data, true));
    } catch {
      out.innerHTML = errorBox("Could not reach the server. Please try again.");
    } finally {
      verifyBtn.disabled = false;
    }
  });
}

/* ---------------- Batch mode ---------------- */
function setupBatch() {
  const input = $("#batchFiles");
  const zone = $("#main");           // re-queried below
  const dz = $("#batchPanel .dropzone-batch");
  const count = $("#batchCount");
  const manifestInput = $("#manifestFile");
  const manifestName = $("#manifestName");
  const btn = $("#batchBtn");
  let files = [];

  function setFiles(list) {
    files = [...list].filter(f => f.type.startsWith("image/"));
    count.textContent = files.length ? `${files.length} photo${files.length > 1 ? "s" : ""} selected` : "or drag them here";
    btn.disabled = files.length === 0;
  }
  input.addEventListener("change", () => setFiles(input.files));
  wireDrop(dz, list => { setFiles(list); });
  manifestInput.addEventListener("change", () => {
    manifestName.textContent = manifestInput.files[0] ? "📄 " + manifestInput.files[0].name : "📄 Choose CSV (optional)";
  });

  btn.addEventListener("click", async () => {
    if (!files.length) return;
    const out = $("#batchResult");
    out.innerHTML = spinner(`Checking ${files.length} labels… this can take a moment.`);
    btn.disabled = true;

    const fd = new FormData();
    files.forEach(f => fd.append("images", f));
    if (manifestInput.files[0]) fd.append("manifest", manifestInput.files[0]);
    applyEngine(fd);

    try {
      const r = await fetch("/api/verify/batch", { method: "POST", body: fd });
      const data = await r.json();
      if (!r.ok) { out.innerHTML = errorBox(data.error || "Something went wrong."); return; }
      out.innerHTML = "";
      out.appendChild(renderBatch(data));
    } catch {
      out.innerHTML = errorBox("Could not reach the server. Please try again.");
    } finally {
      btn.disabled = false;
    }
  });
}

/* ---------------- Rendering ---------------- */
function spinner(text) {
  return `<div class="spinner-wrap"><div class="spinner" role="status" aria-label="Working"></div><span>${esc(text)}</span></div>`;
}
function errorBox(text) { return `<div class="error-box">⚠️ ${esc(text)}</div>`; }

function renderResult(data, showVerdict) {
  const frag = document.createDocumentFragment();

  if (data.error && (!data.fields || data.fields.length === 0)) {
    frag.appendChild(html(errorBox(data.error)));
    return frag;
  }

  if (showVerdict) {
    const v = VERDICT[data.overall] || VERDICT.Review;
    const meta = `${esc(data.engineUsed || "")}${data.processingMs ? " · " + (data.processingMs / 1000).toFixed(1) + "s" : ""}`;
    frag.appendChild(html(`
      <div class="verdict ${v.cls}">
        <span class="verdict-icon">${v.icon}</span>
        <div><h3>${v.title}</h3><p>${v.msg}</p></div>
        <div class="meta">${meta}</div>
      </div>`));
  }

  const checks = document.createElement("div");
  checks.className = "checks";
  // Warning check first — it's the most important.
  checks.appendChild(renderWarning(data.warning));
  (data.fields || []).forEach(f => checks.appendChild(renderCheck(f)));
  frag.appendChild(checks);
  return frag;
}

function renderCheck(f) {
  const cls = "s-" + (f.status || "NotChecked").toLowerCase();
  const compare = (f.expected || f.found)
    ? `<div class="compare">
         ${f.expected ? `<span><b>Application:</b> ${esc(f.expected)}</span>` : ""}
         ${f.found ? `<span><b>On label:</b> ${esc(f.found)}</span>` : ""}
       </div>` : "";
  return html(`
    <div class="check ${cls}">
      <span class="check-ico">${ICONS[f.status] || "➖"}</span>
      <div>
        <div class="check-name">${esc(f.field)}</div>
        ${f.detail ? `<div class="check-detail">${esc(f.detail)}</div>` : ""}
        ${compare}
      </div>
    </div>`);
}

function renderWarning(w) {
  if (!w) return html("");
  const cls = "s-" + (w.status || "Fail").toLowerCase();
  const issues = (w.issues && w.issues.length)
    ? `<ul class="issues">${w.issues.map(i => `<li>${esc(i)}</li>`).join("")}</ul>` : "";
  const found = w.foundText
    ? `<div class="warning-text">${esc(w.foundText)}</div>` : "";
  return html(`
    <div class="check warning-check ${cls}">
      <span class="check-ico">${ICONS[w.status] || "❌"}</span>
      <div>
        <div class="check-name">Government Warning statement</div>
        ${w.detail ? `<div class="check-detail">${esc(w.detail)}</div>` : ""}
        ${issues}
        ${found}
      </div>
    </div>`);
}

function renderBatch(data) {
  const frag = document.createDocumentFragment();
  frag.appendChild(html(`
    <div class="summary">
      <div class="stat pass"><span class="n">${data.pass}</span><span class="lbl">Passed</span></div>
      <div class="stat review"><span class="n">${data.review}</span><span class="lbl">Need a look</span></div>
      <div class="stat fail"><span class="n">${data.fail}</span><span class="lbl">Failed</span></div>
      <div class="stat"><span class="n">${data.total}</span><span class="lbl">Total</span></div>
    </div>`));

  // Toolbar: filter + CSV export
  const toolbar = html(`
    <div class="toolbar">
      <button class="ghost-btn" data-filter="all">Show all</button>
      <button class="ghost-btn" data-filter="Fail">Only failed</button>
      <button class="ghost-btn" data-filter="Review">Only needs-a-look</button>
      <button class="ghost-btn" id="exportCsv">⬇ Download results (CSV)</button>
    </div>`);
  frag.appendChild(toolbar);

  const table = document.createElement("div");
  table.className = "batch-table";

  data.results.forEach((res, idx) => {
    const v = VERDICT[res.overall] || VERDICT.Review;
    const row = html(`
      <div class="batch-row" data-status="${res.overall}" data-idx="${idx}" tabindex="0" role="button" aria-expanded="false">
        <span class="pill ${v.cls}">${v.icon} ${res.overall}</span>
        <span class="fname" title="${esc(res.fileName || "")}">${esc(res.fileName || "(unnamed)")}</span>
        <span class="toggle">details ▾</span>
      </div>`);
    const detail = document.createElement("div");
    detail.className = "batch-detail";
    detail.hidden = true;
    detail.appendChild(renderResult(res, false));

    row.addEventListener("click", () => toggle(row, detail));
    row.addEventListener("keydown", e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(row, detail); } });
    table.appendChild(row);
    table.appendChild(detail);
  });
  frag.appendChild(table);

  // Wire toolbar after nodes exist
  setTimeout(() => {
    $$(".toolbar .ghost-btn[data-filter]").forEach(b =>
      b.addEventListener("click", () => filterRows(table, b.dataset.filter)));
    $("#exportCsv")?.addEventListener("click", () => exportCsv(data.results));
  });

  return frag;
}

function toggle(row, detail) {
  const open = detail.hidden;
  detail.hidden = !open;
  row.setAttribute("aria-expanded", open);
  $(".toggle", row).textContent = open ? "details ▴" : "details ▾";
}
function filterRows(table, filter) {
  $$(".batch-row", table).forEach(row => {
    const show = filter === "all" || row.dataset.status === filter;
    row.style.display = show ? "" : "none";
    if (!show) { const d = row.nextElementSibling; if (d) d.hidden = true; }
  });
}

function exportCsv(results) {
  const head = ["filename", "overall", "engine", "ms", "warning_status", "warning_issues", "field_failures"];
  const rows = results.map(r => {
    const fails = (r.fields || []).filter(f => f.status === "Fail").map(f => f.field).join("; ");
    return [
      r.fileName || "",
      r.overall || "",
      r.engineUsed || "",
      r.processingMs || "",
      r.warning?.status || "",
      (r.warning?.issues || []).join(" | "),
      fails
    ].map(csvCell).join(",");
  });
  const blob = new Blob([head.join(",") + "\n" + rows.join("\n")], { type: "text/csv" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "label-verification-results.csv";
  a.click();
}
function csvCell(s) { s = String(s ?? ""); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; }

/* ---------------- utils ---------------- */
function html(str) { const t = document.createElement("template"); t.innerHTML = str.trim(); return t.content.firstElementChild || document.createTextNode(""); }
function esc(s) { return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }

/* ---------------- boot ---------------- */
document.addEventListener("DOMContentLoaded", () => {
  loadHealth();
  setupTabs();
  setupFirewallToggle();
  setupSingle();
  setupBatch();
});
