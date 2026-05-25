// ImageForge frontend — wires the warm-minimal UI to the real backend.
// Two channels:
//   * POST /api/images          — uploads the file, returns a taskId.
//   * /hub/tasks (SignalR)      — pushes "statusUpdate" events with TaskStatus.
// Initial snapshot for a freshly-created task comes via the SignalR push too,
// because the API broadcasts "pending" right after writing it to Redis.

const $   = (sel) => document.querySelector(sel);
const $$  = (sel) => Array.from(document.querySelectorAll(sel));

// Same-origin requests; in dev frontend is served by the API itself.
const API_BASE = '';

// In-memory state. Refresh wipes it intentionally — there is no server-side
// list endpoint, taskIds the user uploaded only live in this tab.
const tasks = new Map();   // taskId -> task object { id, name, sizeMB, format, sizeOpt, prog, status, error?, resultPath? }
let doneCount = 0;

// Currently selected output options. Updated by chip clicks below.
let selectedFormat = 'webp';
let selectedSize   = '1280';   // "0" means original

// ---- toolbar chips ------------------------------------------------------------

$$('[data-fmt]').forEach((btn) => {
  btn.addEventListener('click', () => {
    $$('[data-fmt]').forEach((b) => b.classList.remove('on'));
    btn.classList.add('on');
    selectedFormat = btn.dataset.fmt;
  });
});

$$('[data-sz]').forEach((btn) => {
  btn.addEventListener('click', () => {
    $$('[data-sz]').forEach((b) => b.classList.remove('on'));
    btn.classList.add('on');
    selectedSize = btn.dataset.sz;
  });
});

// ---- dropzone + file input ----------------------------------------------------

const drop  = $('#drop');
const input = $('#file-input');
const stagingEl = $('#staging');
const thumbsEl  = $('#thumbs');

// Files picked but not yet sent. Each entry: { file, url } where url
// is an objectURL we revoke once the file leaves staging.
let staged = [];

['dragenter', 'dragover'].forEach((ev) => drop.addEventListener(ev, (e) => {
  e.preventDefault();
  drop.classList.add('drag');
}));
['dragleave', 'drop'].forEach((ev) => drop.addEventListener(ev, (e) => {
  e.preventDefault();
  drop.classList.remove('drag');
}));

drop.addEventListener('drop', (e) => {
  if (e.dataTransfer?.files?.length) stageFiles(e.dataTransfer.files);
});

drop.addEventListener('click', () => input.click());
drop.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); input.click(); }
});
input.addEventListener('change', () => {
  if (input.files?.length) stageFiles(input.files);
  input.value = '';   // allow re-selecting the same file later
});

$('#staging-upload').addEventListener('click', () => uploadStaged());
$('#staging-cancel').addEventListener('click', () => clearStaged());

// Move picked files into the staging area. Builds thumbnail previews via
// objectURLs so the browser doesn't read the bytes a second time.
function stageFiles(fileList) {
  for (const file of fileList) {
    staged.push({ file, url: URL.createObjectURL(file) });
  }
  renderStaging();
}

function renderStaging() {
  if (!staged.length) {
    stagingEl.hidden = true;
    thumbsEl.innerHTML = '';
    return;
  }
  stagingEl.hidden = false;
  $('#staging-count').textContent  = staged.length;
  $('#staging-plural').textContent = staged.length === 1 ? '' : 's';

  thumbsEl.innerHTML = '';
  staged.forEach((item, i) => {
    const sizeKb = (item.file.size / 1024).toFixed(0);
    const label = sizeKb >= 1024
      ? (item.file.size / (1024 * 1024)).toFixed(1) + ' MB'
      : sizeKb + ' KB';

    const el = document.createElement('div');
    el.className = 'thumb';
    el.innerHTML = `
      <img alt="" loading="lazy">
      <button class="x" type="button" aria-label="remove">×</button>
      <div class="meta"></div>
    `;
    el.querySelector('img').src = item.url;
    el.querySelector('.meta').textContent = `${item.file.name} · ${label}`;
    el.querySelector('.x').addEventListener('click', (e) => {
      e.stopPropagation();
      URL.revokeObjectURL(staged[i].url);
      staged.splice(i, 1);
      renderStaging();
    });
    thumbsEl.appendChild(el);
  });
}

async function uploadStaged() {
  if (!staged.length) return;
  const toUpload = staged.slice();   // snapshot
  clearStaged();
  await uploadFiles(toUpload.map((s) => s.file));
}

function clearStaged() {
  staged.forEach((s) => URL.revokeObjectURL(s.url));
  staged = [];
  renderStaging();
}

// ---- SignalR connection -------------------------------------------------------

const conn = new signalR.HubConnectionBuilder()
  .withUrl(API_BASE + '/hub/tasks')
  .withAutomaticReconnect()
  .configureLogging(signalR.LogLevel.Warning)
  .build();

conn.on('statusUpdate', (status) => {
  const t = tasks.get(status.taskId);
  if (!t) return;
  applyStatus(t, status);
});

conn.start()
  .then(() => console.log('SignalR connected'))
  .catch((e) => console.error('SignalR connect failed', e));

// ---- uploading ----------------------------------------------------------------

async function uploadFiles(fileList) {
  $('#empty').style.display = 'none';
  for (const file of fileList) {
    try {
      const taskId = await postToApi(file, selectedFormat, selectedSize);
      const t = {
        id:        taskId,
        name:      file.name,
        sizeMB:    file.size / (1024 * 1024),
        format:    selectedFormat,
        sizeOpt:   selectedSize,
        prog:      0,
        status:    'pending',
      };
      tasks.set(taskId, t);
      renderCard(t);
      updateStats();
      // Make sure SignalR has finished its handshake before subscribing.
      if (conn.state === signalR.HubConnectionState.Connected) {
        await conn.invoke('SubscribeToTask', taskId);
      } else {
        conn.start().then(() => conn.invoke('SubscribeToTask', taskId)).catch(console.error);
      }
    } catch (err) {
      console.error('upload failed', err);
      alert('Upload failed: ' + err.message);
    }
  }
}

async function postToApi(file, format, sizeOpt) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('format', format);
  fd.append('maxDimension', sizeOpt);   // "0" => server treats as "no resize"

  const res = await fetch(API_BASE + '/api/images', { method: 'POST', body: fd });
  if (!res.ok) throw new Error(await res.text());
  const json = await res.json();
  return json.taskId;
}

// ---- rendering ----------------------------------------------------------------

const cardsEl = $('#cards');

function renderCard(t) {
  const el = document.createElement('div');
  el.className = 'card';
  el.id = 'card-' + t.id;

  const sizeLabel = t.sizeOpt === '0' ? 'original' : (t.sizeOpt + 'px');
  el.innerHTML = `
    <div class="card-top">
      <div style="min-width:0">
        <div class="fname"></div>
        <div class="fmeta"></div>
      </div>
      <span class="status s-pending"><span class="sq"></span>pending</span>
    </div>
    <div class="pbar"><i></i><span class="pk"></span></div>
    <div class="prow"><span class="phase">queued</span><span class="pct">0%</span></div>
    <div class="compare">
      <div class="layer before"></div>
      <div class="layer after"></div>
      <div class="ctag l">before</div><div class="ctag r">after</div>
      <div class="handle"><div class="knob">‹ ›</div></div>
    </div>
    <div class="result"></div>
  `;
  el.querySelector('.fname').textContent = t.name;
  el.querySelector('.fmeta').textContent = `${t.sizeMB.toFixed(1)} MB · → ${t.format} · ${sizeLabel}`;
  cardsEl.prepend(el);
}

function applyStatus(t, status) {
  t.status      = status.state;
  t.prog        = status.progress;
  t.resultPath  = status.resultPath ?? null;
  t.error       = status.error ?? null;

  const el = document.getElementById('card-' + t.id);
  if (!el) return;

  // status dot/text
  const sp = el.querySelector('.status');
  sp.className = 'status s-' + t.status;
  sp.innerHTML = `<span class="sq"></span>${t.status}`;

  // progress bar
  el.querySelector('.pbar i').style.width  = t.prog + '%';
  el.querySelector('.pk').style.left       = t.prog + '%';
  el.querySelector('.pct').textContent     = Math.round(t.prog) + '%';

  // phase label — synthesized from progress so the user feels the stages
  el.querySelector('.phase').textContent = phaseLabel(t);

  if (t.status === 'done') {
    doneCount++;
    el.querySelector('.phase').textContent = 'completed';
    showResult(t, el);
  } else if (t.status === 'failed') {
    showError(t, el);
  }

  updateStats();
}

function phaseLabel(t) {
  if (t.status === 'pending')    return 'queued';
  if (t.status === 'failed')     return 'failed';
  if (t.prog < 25)               return 'starting';
  if (t.prog < 60)               return 'decoding';
  if (t.prog < 90)               return 'resizing';
  if (t.prog < 100)              return 'encoding ' + t.format;
  return 'completed';
}

function showResult(t, el) {
  const compare = el.querySelector('.compare');
  const sourceUrl = `${API_BASE}/api/images/${t.id}/source`;
  const resultUrl = `${API_BASE}/api/images/${t.id}/result`;

  el.querySelector('.layer.before').style.backgroundImage = `url('${sourceUrl}')`;
  el.querySelector('.layer.after').style.backgroundImage  = `url('${resultUrl}')`;
  compare.classList.add('show');
  initSlider(compare);

  el.querySelector('.result').innerHTML = `
    <a class="dl" href="${resultUrl}" download>↓ Download ${t.format}</a>
  `;
}

function showError(t, el) {
  const r = el.querySelector('.result');
  r.innerHTML = `<div class="err">Error: ${escapeHtml(t.error ?? 'unknown')}</div>`;
}

function initSlider(cmp) {
  if (cmp.dataset.wired === '1') return;
  cmp.dataset.wired = '1';
  const after  = cmp.querySelector('.after');
  const handle = cmp.querySelector('.handle');
  let dragging = false;

  const move = (x) => {
    const r = cmp.getBoundingClientRect();
    let p = (x - r.left) / r.width;
    p = Math.max(0, Math.min(1, p));
    after.style.clipPath = `inset(0 0 0 ${p * 100}%)`;
    handle.style.left    = (p * 100) + '%';
  };

  cmp.addEventListener('mousedown', (e) => { dragging = true; move(e.clientX); });
  window.addEventListener('mousemove', (e) => { if (dragging) move(e.clientX); });
  window.addEventListener('mouseup', () => { dragging = false; });
  cmp.addEventListener('touchmove', (e) => move(e.touches[0].clientX), { passive: true });
}

function updateStats() {
  const all = Array.from(tasks.values());
  const inflight = all.filter((t) => t.status !== 'done' && t.status !== 'failed').length;
  const finished = all.length - inflight;

  $('#qcount').textContent = inflight;
  $('#dcount').textContent = doneCount;
  $('#qlabel').textContent = all.length
    ? `${all.length} total · ${inflight} in flight`
    : 'no tasks yet';

  // Show the clear button only when there's something to clear.
  const clearBtn = $('#clear-btn');
  clearBtn.hidden = finished === 0;
  clearBtn.textContent = `clear completed (${finished})`;
}

// Clear button: drops every finished task (done or failed) from the UI.
// Server-side state and stored files are untouched; only the in-memory
// view here is cleaned up.
$('#clear-btn').addEventListener('click', () => {
  for (const [id, t] of tasks) {
    if (t.status === 'done' || t.status === 'failed') {
      const el = document.getElementById('card-' + id);
      if (el) el.remove();
      tasks.delete(id);
    }
  }
  if (tasks.size === 0) {
    $('#empty').style.display = '';
  }
  updateStats();
});

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}

// ---- Worker fleet — poll /api/stats every 2s ---------------------------------

async function refreshStats() {
  try {
    const res = await fetch(API_BASE + '/api/stats');
    if (!res.ok) return;
    const s = await res.json();

    document.getElementById('stat-consumers').textContent = s.available ? s.consumers : '—';
    document.getElementById('stat-ready').textContent     = s.available ? s.messagesReady : '—';
    document.getElementById('stat-inflight').textContent  = s.available ? s.messagesUnacknowledged : '—';

    // Visual pulse: when consumers > 0, treat the workers row as "busy" so
    // the dot pulses; when there is in-flight work, do the same for that row.
    document.getElementById('wrk-consumers').classList.toggle('busy', s.consumers > 0);
    document.getElementById('wrk-inflight').classList.toggle('busy', s.messagesUnacknowledged > 0);
  } catch {
    // Silently ignore — stats are best-effort.
  }
}
refreshStats();
setInterval(refreshStats, 2000);

// ---- Lifetime stats — poll every 5s -----------------------------------------

async function refreshLifetime() {
  try {
    const res = await fetch(API_BASE + '/api/lifetime-stats');
    if (!res.ok) return;
    const { processed, bytesIn, bytesOut } = await res.json();

    document.getElementById('lt-processed').textContent = formatNumber(processed);

    const saved = bytesIn - bytesOut;
    document.getElementById('lt-saved').textContent = saved > 0 ? formatBytes(saved) : '—';

    const ratio = bytesIn > 0 ? Math.round((bytesOut / bytesIn) * 100) : 0;
    const ratioEl = document.getElementById('lt-ratio');
    ratioEl.textContent = bytesIn > 0 ? (ratio + '%') : '—';
    ratioEl.classList.toggle('accent', bytesIn > 0);
  } catch { /* best-effort */ }
}

function formatNumber(n) {
  return n.toLocaleString('en-US');
}

function formatBytes(n) {
  if (n >= 1024 * 1024 * 1024) return (n / (1024 ** 3)).toFixed(2) + ' GB';
  if (n >= 1024 * 1024)        return (n / (1024 ** 2)).toFixed(1) + ' MB';
  if (n >= 1024)               return (n / 1024).toFixed(0) + ' KB';
  return n + ' B';
}

refreshLifetime();
setInterval(refreshLifetime, 5000);

// ---- Theme toggle ------------------------------------------------------------

const themeBtn = $('#theme-toggle');
themeBtn?.addEventListener('click', () => {
  const html = document.documentElement;
  const next = html.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
  if (next === 'dark') {
    html.setAttribute('data-theme', 'dark');
  } else {
    html.removeAttribute('data-theme');
  }
  try { localStorage.setItem('imageforge-theme', next); } catch {}
});
