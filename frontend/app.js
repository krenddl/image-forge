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

['dragenter', 'dragover'].forEach((ev) => drop.addEventListener(ev, (e) => {
  e.preventDefault();
  drop.classList.add('drag');
}));
['dragleave', 'drop'].forEach((ev) => drop.addEventListener(ev, (e) => {
  e.preventDefault();
  drop.classList.remove('drag');
}));

drop.addEventListener('drop', (e) => {
  if (e.dataTransfer?.files?.length) uploadFiles(e.dataTransfer.files);
});

drop.addEventListener('click', () => input.click());
drop.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); input.click(); }
});
input.addEventListener('change', () => {
  if (input.files?.length) uploadFiles(input.files);
  input.value = '';   // allow re-selecting the same file later
});

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
  const inflight = Array.from(tasks.values()).filter((t) => t.status !== 'done' && t.status !== 'failed').length;
  $('#qcount').textContent = inflight;
  $('#dcount').textContent = doneCount;
  const total = tasks.size;
  $('#qlabel').textContent = total
    ? `${total} total · ${inflight} in flight`
    : 'no tasks yet';
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}
