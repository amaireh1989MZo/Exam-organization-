'use strict';
// واجهة المرحلة 1: لوحة الحالة ومجلد الشبكة. بلا مكتبات خارجية.
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
// أرقام غربية: الصفر العربي (٠) يبدو كنقطة في البطاقات الكبيرة فيوحي بأنها فارغة.
const LOCALE = 'ar-EG-u-nu-latn';
const fmt = n => Number(n || 0).toLocaleString(LOCALE);
const el = (tag, props = {}, ...kids) => {
  const e = Object.assign(document.createElement(tag), props);
  for (const k of kids) e.append(k);
  return e;
};

// التواريخ من القاعدة تصل بلا منطقة زمنية وهي UTC؛ نضيف Z إذا لم تكن موجودة.
const utc = s => new Date(/(Z|[+-]\d\d:\d\d)$/.test(s) ? s : s + 'Z').toLocaleString(LOCALE);
const STATUS_AR = { Running: 'قيد التشغيل', Completed: 'مكتملة', Cancelled: 'أوقفت نهائيًا', Failed: 'فشلت', Queued: 'بالانتظار', Processing: 'قيد المعالجة' };

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    // X-QB-Client: حماية CSRF؛ الخادم يرفض أي طلب يغيّر شيئًا بدونها.
    headers: Object.assign({ 'X-QB-Client': 'web' }, body ? { 'Content-Type': 'application/json' } : {}),
    body: body ? JSON.stringify(body) : undefined
  });
  if (res.status === 403) { $('#denied').hidden = false; throw new Error('forbidden'); }
  let data = null;
  try { data = await res.json(); } catch { /* 204 */ }
  if (!res.ok) throw Object.assign(new Error(data?.message || 'تعذّر تنفيذ الطلب.'), { status: res.status });
  return data;
}

/* ---------- toast ---------- */
let toastTimer, undoFn;
function toast(msg, undo) {
  $('#toast-msg').textContent = msg;
  undoFn = undo || null;
  $('#toast-undo').hidden = !undo;
  $('#toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('#toast').hidden = true; }, 5000);
}
$('#toast-undo').onclick = () => { $('#toast').hidden = true; undoFn?.(); };

/* ---------- navigation ---------- */
function show(view) {
  $$('[data-screen]').forEach(s => { s.hidden = s.dataset.screen !== view; });
  $$('.nav[data-view]').forEach(n => n.dataset.view === view ? n.setAttribute('aria-current', 'page') : n.removeAttribute('aria-current'));
  $('#denied').hidden = true;
  history.replaceState(null, '', '#' + view);
  if (view === 'dash') refreshStatus();
  if (view === 'folders') { loadRoot(); loadQueue(); }
  if (view === 'drop') loadUploads();
  if (view === 'review') loadReview(true);
  if (view === 'export') loadExport();
  if (view === 'dups') loadDups();
  if (view === 'classify') loadFrameworks();
}
$$('.nav[data-view]').forEach(n => n.addEventListener('click', () => show(n.dataset.view)));

/* ---------- user ---------- */
const ROLE_AR = { Admin: 'مدير النظام', BankManager: 'مسؤول بنك الأسئلة', Reviewer: 'مراجع فني', Viewer: 'اطلاع' };
async function loadMe() {
  try {
    const me = await api('GET', '/api/me');
    $('#who-name').textContent = me.name || '';
    $('#who-roles').textContent = (me.roles || []).map(r => ROLE_AR[r] || r).join('، ');
  } catch { /* handled by banner */ }
}

/* ---------- dashboard ---------- */
let pollTimer;
async function refreshStatus() {
  clearTimeout(pollTimer);
  let data;
  try { data = await api('GET', '/api/inventory/status'); } catch { return; }
  const { worker, status } = data;
  const by = status.filesByStatus || {};

  $('#s-total').textContent = fmt(status.filesTotal);
  $('#s-pending').textContent = fmt((by.Pending || 0) + (by.Processing || 0));
  $('#s-completed').textContent = fmt(by.Completed);
  $('#s-failed').textContent = fmt(by.Failed);
  $('#s-missing').textContent = fmt(status.filesMissing);
  $('#s-total-sub').textContent = by.NeedsReview ? `${fmt(by.NeedsReview)} تحتاج مراجعة` : '';

  const problem = $('#problem');
  problem.hidden = !worker.problem;
  problem.textContent = worker.problem || '';

  const run = status.activeRun || status.lastFinishedRun;
  const active = !!status.activeRun;
  $('#btn-scan').disabled = worker.scanning;
  $('#btn-scan').textContent = worker.scanning ? 'الجرد يعمل…' : active ? 'استئناف الجرد' : 'بدء الجرد';
  $('#btn-abandon').hidden = !active;
  if (!active) $('#abandon-confirm').hidden = true;

  renderRun(run, active, worker.scanning);
  renderExtensions(status.filesByExtension || {});

  pollTimer = setTimeout(() => { if (!$('[data-screen="dash"]').hidden) refreshStatus(); }, active || worker.scanning ? 2000 : 15000);
}

function renderRun(run, active, scanning) {
  const body = $('#run-body');
  if (!run) {
    $('#run-title').textContent = 'عملية الجرد';
    body.className = 'muted';
    body.textContent = 'لم يُجرَ أي جرد بعد. اضغط «بدء الجرد».';
    return;
  }
  $('#run-title').textContent = active ? `عملية الجرد رقم ${fmt(run.id)} (جارية)` : `آخر عملية جرد: رقم ${fmt(run.id)}`;
  body.className = '';
  body.replaceChildren();

  const chipClass = run.status === 'Completed' ? 'ok' : run.status === 'Running' ? (scanning ? 'run' : 'warn') : 'wait';
  const chipText = run.status === 'Running' && !scanning ? 'متوقفة مؤقتًا، تُستأنف تلقائيًا' : STATUS_AR[run.status] || run.status;
  body.append(el('span', { className: 'chip ' + chipClass, textContent: chipText }));

  if (active) {
    const done = run.directoriesCompleted + run.directoriesFailed;
    const total = done + run.directoriesPending;
    const pct = total ? Math.round(done / total * 100) : 0;
    const bar = el('div', { className: 'bar' }, el('span'));
    bar.style.marginTop = '12px';
    bar.firstChild.style.width = pct + '%';
    body.append(bar, el('div', { className: 'muted', textContent: `${fmt(done)} من ${fmt(total)} مجلد معروف حتى الآن` }));
  }

  const dl = el('dl', { className: 'kv' });
  const rows = [
    ['بدأت', utc(run.startedAtUtc)],
    run.finishedAtUtc ? ['انتهت', utc(run.finishedAtUtc)] : null,
    ['ملفات شوهدت', fmt(run.filesSeen)],
    ['جديدة', fmt(run.filesNew)],
    ['تغيّرت', fmt(run.filesChanged)],
    ['مفقودة', fmt(run.filesMissing)],
    ['مجلدات تعذّر جردها', fmt(run.directoriesFailed)],
    ['روابط رمزية تُجوهلت', fmt(run.linksSkipped)],
    ['ملفات تعذّرت قراءة معلوماتها', fmt(run.entryErrors)]
  ].filter(Boolean);
  for (const [k, v] of rows) dl.append(el('dt', { textContent: k }), el('dd', { textContent: v }));
  body.append(dl);
}

function renderExtensions(exts) {
  const box = $('#exts');
  const entries = Object.entries(exts);
  if (!entries.length) { box.className = 'muted'; box.textContent = 'لا توجد ملفات بعد.'; return; }
  box.className = '';
  box.replaceChildren();
  const max = Math.max(...entries.map(e => e[1]));
  for (const [ext, count] of entries) {
    const bar = el('div', { className: 'bar' }, el('span'));
    bar.firstChild.style.width = (count / max * 100) + '%';
    box.append(el('div', { className: 'fmt' },
      el('span', { className: 'mono', textContent: ext }), bar,
      el('span', { className: 'mono muted', textContent: fmt(count) })));
  }
}

$('#btn-scan').onclick = async () => {
  try { await api('POST', '/api/inventory/scan'); toast('طُلب بدء الجرد.'); setTimeout(refreshStatus, 500); }
  catch (e) { toast(e.message); }
};
$('#btn-report').onclick = async () => {
  const b = $('#btn-report');
  b.disabled = true; b.textContent = 'يُنشأ التقرير…';
  try {
    const r = await api('POST', '/api/reports/excel');
    toast(`أُنشئ التقرير: ${fmt(r.questions)} سؤال و${fmt(r.files)} ملف.`);
    location.href = '/api/reports/' + encodeURIComponent(r.fileName);
  } catch (e) { toast(e.message); }
  finally { b.disabled = false; b.textContent = 'تقرير Excel'; }
};
$('#btn-abandon').onclick = () => { $('#abandon-confirm').hidden = false; $('#btn-abandon').hidden = true; };
$('#abandon-no').onclick = () => { $('#abandon-confirm').hidden = true; $('#btn-abandon').hidden = false; };
$('#abandon-yes').onclick = async () => {
  $('#abandon-confirm').hidden = true;
  try { await api('POST', '/api/inventory/abandon'); toast('أُوقفت عملية الجرد نهائيًا.'); setTimeout(refreshStatus, 800); }
  catch (e) { toast(e.message); }
};

/* ---------- folders ---------- */
async function loadRoot() {
  let listing;
  try { listing = await api('GET', '/api/inventory/tree?path='); } catch { return; }
  const tree = $('#tree');
  tree.replaceChildren(...listing.folders.map(nodeItem));
  $('#tree-empty').hidden = listing.folders.length > 0;
}

function nodeItem(f) {
  const li = el('li', { role: 'treeitem' });
  li.setAttribute('aria-expanded', 'false');
  const toggle = el('button', { className: 'toggle', type: 'button', textContent: '◂', title: 'عرض المجلدات الفرعية' });
  toggle.setAttribute('aria-hidden', f.hasChildren ? 'false' : 'true');
  toggle.setAttribute('aria-label', 'توسيع ' + f.name);
  if (!f.hasChildren) toggle.tabIndex = -1;

  const done = f.completed, bad = f.failed, rev = f.needsReview, total = f.fileCount || 1;
  const mini = el('span', { className: 'mini', title: `${fmt(done)} مكتمل · ${fmt(rev)} تحتاج مراجعة · ${fmt(bad)} فشل · ${fmt(f.pending)} بالانتظار` },
    seg('m-ok', done / total), seg('m-rev', rev / total), seg('m-bad', bad / total));
  const add = el('button', { className: 'btn sm', type: 'button', textContent: 'أضف' });
  add.setAttribute('aria-label', 'أضف ' + f.name + ' إلى قائمة المعالجة');

  const node = el('div', { className: 'node', draggable: true },
    toggle,
    el('span', { textContent: '📁', ariaHidden: 'true' }),
    el('span', { className: 'name', textContent: f.name }),
    el('span', { className: 'meta' }, el('span', { className: 'c', textContent: fmt(f.fileCount) + ' ملف' }), mini, add));
  node.dataset.path = f.relativePath;
  li.append(node);

  node.addEventListener('dragstart', e => {
    e.dataTransfer.setData('text/x-qb-folder', f.relativePath);
    e.dataTransfer.effectAllowed = 'copy';
    node.classList.add('dragging');
  });
  node.addEventListener('dragend', () => node.classList.remove('dragging'));
  add.addEventListener('click', () => enqueue(f.relativePath));
  toggle.addEventListener('click', () => toggleNode(li, f));
  return li;
}

function seg(cls, ratio) {
  const i = el('i', { className: cls });
  i.style.width = (ratio * 100) + '%';
  return i;
}

async function toggleNode(li, f) {
  const open = li.getAttribute('aria-expanded') === 'true';
  li.querySelector(':scope > ul')?.remove();
  li.setAttribute('aria-expanded', String(!open));
  li.querySelector('.toggle').textContent = open ? '◂' : '▾';
  if (open) return;
  try {
    const listing = await api('GET', '/api/inventory/tree?path=' + encodeURIComponent(f.relativePath));
    li.append(el('ul', { role: 'group' }, ...listing.folders.map(nodeItem)));
  } catch (e) { toast(e.message); }
}

/* ---------- queue ---------- */
const queueBox = $('#queue');
['dragenter', 'dragover'].forEach(t => queueBox.addEventListener(t, e => {
  if (!e.dataTransfer.types.includes('text/x-qb-folder')) return;
  e.preventDefault();
  queueBox.classList.add('over');
}));
['dragleave', 'drop'].forEach(t => queueBox.addEventListener(t, () => queueBox.classList.remove('over')));
queueBox.addEventListener('drop', e => {
  const path = e.dataTransfer.getData('text/x-qb-folder');
  if (path) { e.preventDefault(); enqueue(path); }
});

async function enqueue(path) {
  try {
    const before = (await api('GET', '/api/queue')).length;
    const entry = await api('POST', '/api/queue', { path });
    await loadQueue();
    const after = Number($('#n-queue').textContent);
    if (after === before) toast(`«${entry.relativePath}» موجود في القائمة مسبقًا.`);
    else toast(`أُضيف «${entry.relativePath}» (${fmt(entry.fileCount)} ملف).`, () => cancel(entry.id, true));
  } catch (e) { toast(e.message); }
}

async function cancel(id, silent) {
  try {
    await api('DELETE', '/api/queue/' + id);
    await loadQueue();
    if (!silent) toast('أُزيل الطلب من القائمة.');
  } catch (e) { toast(e.message); }
}

async function loadQueue() {
  let items;
  try { items = await api('GET', '/api/queue'); } catch { return; }
  $('#n-queue').textContent = items.length;
  queueBox.replaceChildren();
  if (!items.length) { queueBox.append(el('div', { className: 'empty', textContent: 'أفلت مجلدًا هنا' })); return; }
  items.forEach((q, i) => {
    const remove = el('button', { className: 'btn sm', type: 'button', textContent: 'إزالة' });
    remove.setAttribute('aria-label', 'إزالة ' + (q.relativePath || 'الجذر') + ' من القائمة');
    remove.onclick = () => cancel(q.id);
    if (q.status !== 'Queued') remove.disabled = true;
    queueBox.append(el('div', { className: 'qitem' },
      el('div', { className: 'row' },
        el('b', { textContent: `${fmt(i + 1)}. ${q.relativePath || 'المجلد كاملًا'}` }),
        el('span', { className: 'chip ' + (q.status === 'Queued' ? 'wait' : 'run'), textContent: STATUS_AR[q.status] || q.status })),
      el('div', { className: 'row' },
        el('span', { className: 'meta' }, `${fmt(q.fileCount)} ملف · `, el('bdi', { textContent: q.requestedBy }), ' · ', el('bdi', { textContent: utc(q.requestedAtUtc) })),
        remove)));
  });
}

/* ---------- drop & upload ---------- */
const STATUS_FILE_AR = { Pending: 'بانتظار قارئ لهذه الصيغة', Completed: 'تم', Failed: 'فشل', Processing: 'يُعالج', NeedsReview: 'تحتاج مطابقة أعمدة' };
const ERR_AR = { ReaderError: 'تعذّرت قراءة بنية الملف' };
let uploadInfo = null; // يُحمَّل من الخادم؛ لا نحكم على صيغة ملف قبل وصوله
let selectedUpload = null;

async function loadUploads() {
  let data;
  try { data = await api('GET', '/api/uploads'); } catch { return; }
  uploadInfo = data;
  $('#drop-hint').textContent = `أو اضغط للاختيار · ${data.allowedExtensions.join(' · ').toUpperCase()} · حتى ${fmt(data.maxFileSizeMb)} ميغابايت`;
  $('#readable-note').textContent = `الأسئلة تُستخرج الآن من: ${data.readableExtensions.join('، ').toUpperCase()}. باقي الصيغ تُحفظ وتنتظر قارئها.`;
  const list = $('#file-list');
  list.replaceChildren();
  if (!data.items.length) { list.append(el('div', { className: 'empty', textContent: 'لم يُرفع أي ملف بعد.' })); return; }
  data.items.forEach(u => list.append(uploadCard(u)));
}

function uploadCard(u) {
  const card = el('button', { className: 'file', type: 'button' });
  const chip = statusChip(u);
  card.append(
    el('span', { className: 'ext', textContent: (u.extension || '?').slice(0, 4) }),
    el('span', {}, el('span', { className: 'nm', textContent: u.clientPath || u.originalName }),
      el('br'), el('span', { className: 'meta' }, `${fmt(Math.ceil(u.sizeBytes / 1024))} KB · `, el('bdi', { textContent: u.uploadedBy }))),
    chip);
  card.dataset.id = u.id;
  if (selectedUpload === u.id) card.setAttribute('aria-current', 'true');
  card.addEventListener('click', () => showQuestions(u));
  return card;
}

function statusChip(u) {
  if (u.status === 'Completed') return el('span', { className: 'chip ok', textContent: `تم · ${fmt(u.questionCount)} سؤال` });
  if (u.status === 'Failed') return el('span', { className: 'chip bad', textContent: ERR_AR[u.lastErrorCode] || 'فشل' });
  if (u.status === 'NeedsReview') return el('span', { className: 'chip warn', textContent: 'تحتاج مطابقة أعمدة' });
  return el('span', { className: 'chip wait', textContent: STATUS_FILE_AR[u.status] || u.status });
}

async function showQuestions(u) {
  selectedUpload = u.id;
  $$('#file-list .file').forEach(c => c.dataset.id == u.id ? c.setAttribute('aria-current', 'true') : c.removeAttribute('aria-current'));
  $('#qpanel-title').textContent = `الأسئلة المستخرجة · ${u.originalName}`;
  const body = $('#qpanel-body');
  if (u.status === 'NeedsReview' && u.lastErrorCode === 'NeedsColumnMapping') { showMapping(u); return; }
  if (u.status !== 'Completed') {
    body.className = 'muted';
    body.textContent = u.status === 'Pending' ? 'حُفظ الملف، وسيُستخرج عند تفعيل قارئ هذه الصيغة.' : 'تعذّر الاستخراج من هذا الملف.';
    return;
  }
  let questions;
  try { questions = await api('GET', '/api/questions?uploadId=' + u.id); } catch (e) { toast(e.message); return; }
  body.className = '';
  body.replaceChildren();
  if (!questions.length) { body.className = 'muted'; body.textContent = 'لم تُكتشف أسئلة في هذا الملف.'; return; }
  const CONF_AR = { High: 'ثقة عالية', Medium: 'ثقة متوسطة', Low: 'تحتاج تدخلًا' };
  for (const q of questions) {
    const ol = el('ol');
    q.options.forEach((o, i) => ol.append(el('li', { className: i === q.correctIndex ? 'correct' : '', textContent: o })));
    const dot = el('span', { className: 'dot ' + q.confidence, title: CONF_AR[q.confidence] });
    body.append(el('div', { className: 'q' },
      el('div', { className: 'qh' }, el('span', { className: 'qt', textContent: `${fmt(q.order)}. ${q.text}` }), dot),
      ol,
      q.issues.length ? el('div', { className: 'issues', textContent: q.issues.join(' · ') }) : '',
      el('div', { className: 'loc', textContent: `${q.location}${q.code ? ' · ' + q.code : ''} · ${CONF_AR[q.confidence]}` })));
  }
}

/* ---------- Excel column mapping ---------- */
const MAP_SLOTS = [['stem', 'نص السؤال', true], ['o0', 'الخيار أ', true], ['o1', 'الخيار ب', true], ['o2', 'الخيار ج'], ['o3', 'الخيار د'],
  ['o4', 'الخيار هـ'], ['o5', 'الخيار و'], ['answer', 'الإجابة الصحيحة'], ['code', 'رمز السؤال']];

async function showMapping(u) {
  const body = $('#qpanel-body');
  $('#qpanel-title').textContent = `مطابقة أعمدة · ${u.originalName}`;
  body.className = '';
  body.replaceChildren(el('div', { className: 'muted', textContent: 'جارٍ تحميل المعاينة…' }));
  let sheets;
  try { sheets = await api('GET', `/api/uploads/${u.id}/preview`); } catch (e) { body.textContent = e.message; return; }
  const st = { sheet: 0, headerRow: 0, map: {}, picked: null };
  const render = () => renderMapping(u, sheets, st);
  st.render = render;
  render();
}

function renderMapping(u, sheets, st) {
  const body = $('#qpanel-body');
  body.replaceChildren();
  const sheet = sheets[st.sheet];
  if (!sheet || !sheet.rows.length) { body.append(el('div', { className: 'muted', textContent: 'الملف لا يحتوي بيانات.' })); return; }
  body.append(el('p', { className: 'note', textContent: 'لم يتعرف النظام على أعمدة هذا الملف تلقائيًا. اسحب كل عمود إلى معناه مرة واحدة، ويُحفظ قالبًا يُطبَّق على كل ملف له العناوين نفسها.' }));

  if (sheets.length > 1) {
    const sel = el('select', { id: 'map-sheet' });
    sheets.forEach((s, i) => sel.append(el('option', { value: i, textContent: s.name, selected: i === st.sheet })));
    sel.onchange = () => { st.sheet = Number(sel.value); st.headerRow = 0; st.map = {}; st.render(); };
    body.append(el('label', { className: 'field' }, 'الورقة:', sel));
  }
  const rowSel = el('select', { id: 'map-row' });
  sheet.rows.forEach((r, i) => rowSel.append(el('option', { value: i, textContent: `صف ${r.rowNo}: ${r.cells.filter(Boolean).slice(0, 3).join(' · ')}`, selected: i === st.headerRow })));
  rowSel.onchange = () => { st.headerRow = Number(rowSel.value); st.map = {}; st.render(); };
  body.append(el('label', { className: 'field' }, 'صف العناوين:', rowSel));

  const headers = sheet.rows[st.headerRow].cells;
  const used = new Set(Object.values(st.map));
  const palette = el('div', { className: 'cols' });
  headers.forEach((h, col) => {
    if (!h || used.has(col)) return;
    const chip = el('span', { className: 'colchip' + (st.picked === col ? ' picked' : ''), draggable: true, textContent: h, tabIndex: 0 });
    chip.addEventListener('dragstart', e => e.dataTransfer.setData('text/x-qb-col', String(col)));
    chip.onclick = () => { st.picked = st.picked === col ? null : col; st.render(); };
    palette.append(chip);
  });
  if (!palette.children.length) palette.append(el('span', { className: 'muted', textContent: 'استُخدمت كل الأعمدة' }));
  body.append(palette);

  const slots = el('div', { className: 'slots' });
  for (const [key, label, required] of MAP_SLOTS) {
    const col = st.map[key];
    const slot = el('div', { className: 'slot' + (required ? ' req' : '') + (col !== undefined ? ' filled' : '') }, el('span', { className: 'lbl', textContent: label }));
    if (col !== undefined) {
      const chip = el('span', { className: 'colchip', textContent: headers[col], title: 'اضغط للإزالة' });
      chip.onclick = e => { e.stopPropagation(); delete st.map[key]; st.render(); };
      slot.append(chip);
    } else slot.append(el('span', { className: 'muted', style: 'font-size:12px', textContent: 'أفلت عمودًا' }));
    ['dragenter', 'dragover'].forEach(t => slot.addEventListener(t, e => { e.preventDefault(); slot.classList.add('over'); }));
    slot.addEventListener('dragleave', () => slot.classList.remove('over'));
    slot.addEventListener('drop', e => { e.preventDefault(); const c = Number(e.dataTransfer.getData('text/x-qb-col')); if (!Number.isNaN(c)) { st.map[key] = c; st.render(); } });
    slot.onclick = () => { if (st.picked !== null) { st.map[key] = st.picked; st.picked = null; st.render(); } };
    slots.append(slot);
  }
  body.append(slots);

  const mapped = MAP_SLOTS.filter(([k]) => st.map[k] !== undefined);
  if (mapped.length) {
    const table = el('table');
    table.append(el('thead', {}, el('tr', {}, ...mapped.map(([, label]) => el('th', { textContent: label })))));
    const tbody = el('tbody');
    sheet.rows.slice(st.headerRow + 1, st.headerRow + 4).forEach(r =>
      tbody.append(el('tr', {}, ...mapped.map(([k]) => el('td', { textContent: r.cells[st.map[k]] || '' })))));
    table.append(tbody);
    body.append(el('div', { className: 'tablewrap' }, table));
  }

  const name = el('input', { id: 'map-name', value: st.name ?? u.originalName.replace(/\.xlsx$/i, ''), size: 30 });
  name.oninput = () => { st.name = name.value; };
  const save = el('button', { className: 'btn primary', type: 'button', textContent: 'حفظ القالب واستخراج الأسئلة' });
  save.onclick = () => saveTemplate(u, headers, st, name.value);
  body.append(el('label', { className: 'field' }, 'اسم القالب:', name), el('div', { className: 'actions' }, save));
}

async function saveTemplate(u, headers, st, name) {
  const h = c => (st.map[c] === undefined ? null : headers[st.map[c]]);
  const options = ['o0', 'o1', 'o2', 'o3', 'o4', 'o5'].map(h).filter(Boolean);
  try {
    const r = await api('POST', '/api/templates', { name, headers, stem: h('stem'), options, answer: h('answer'), code: h('code') });
    const view = await api('POST', `/api/uploads/${u.id}/reextract`);
    toast(`حُفظ القالب «${r.template.name}» واستُخرج ${fmt(view.questionCount)} سؤال` +
      (r.requeuedSourceFiles ? `، وأُعيد ${fmt(r.requeuedSourceFiles)} ملفًا من مجلد الشبكة إلى الطابور.` : '.'));
    await loadUploads();
    showQuestions(view);
  } catch (e) { toast(e.message); }
}

const dz = $('#dropzone');
['dragenter', 'dragover'].forEach(t => dz.addEventListener(t, e => { e.preventDefault(); dz.classList.add('over'); }));
['dragleave', 'drop'].forEach(t => dz.addEventListener(t, e => { e.preventDefault(); dz.classList.remove('over'); }));
dz.addEventListener('drop', async e => {
  const items = [...(e.dataTransfer.items || [])];
  const files = [];
  if (items.length && items[0].webkitGetAsEntry) {
    for (const it of items) { const entry = it.webkitGetAsEntry(); if (entry) await walk(entry, '', files); }
  } else {
    [...e.dataTransfer.files].forEach(f => files.push({ file: f, path: f.name }));
  }
  uploadAll(files);
});
$('#picker').addEventListener('change', e => { uploadAll([...e.target.files].map(f => ({ file: f, path: f.name }))); e.target.value = ''; });

async function walk(entry, prefix, out) {
  if (entry.isFile) {
    await new Promise(res => entry.file(f => { out.push({ file: f, path: prefix + f.name }); res(); }, res));
  } else if (entry.isDirectory) {
    const reader = entry.createReader();
    let batch;
    do {
      batch = await new Promise(res => reader.readEntries(res, () => res([])));
      for (const child of batch) await walk(child, prefix + entry.name + '/', out);
    } while (batch.length);
  }
}

async function uploadAll(files) {
  if (!files.length) return;
  if (!uploadInfo) await loadUploads();
  if (!uploadInfo) { toast('تعذّر تحميل إعدادات الرفع. أعد المحاولة.'); return; }
  const list = $('#file-list');
  list.querySelector('.empty')?.remove();
  // ملف واحد في كل مرة: الخادم يستخرج فورًا، والتقدم واضح لكل ملف.
  for (const { file, path } of files) {
    const ext = (file.name.split('.').pop() || '').toLowerCase();
    const bar = el('div', { className: 'bar' }, el('span'));
    const chip = el('span', { className: 'chip run', textContent: 'يُرفع…' });
    const meta = el('span', { className: 'meta', textContent: `${fmt(Math.ceil(file.size / 1024))} KB` });
    const card = el('div', { className: 'file' },
      el('span', { className: 'ext', textContent: ext.slice(0, 4) || '?' }),
      el('span', {}, el('span', { className: 'nm', textContent: path }), el('br'), meta, bar), chip);
    list.prepend(card);

    if (!uploadInfo.allowedExtensions.includes(ext)) { finish(card, chip, bar, 'bad', 'صيغة غير مقبولة'); continue; }
    if (file.size > uploadInfo.maxFileSizeMb * 1024 * 1024) { finish(card, chip, bar, 'bad', 'أكبر من الحد المسموح'); continue; }
    try {
      const res = await uploadOne(file, path, p => { bar.firstChild.style.width = Math.round(p * 100) + '%'; });
      if (res.archive) {
        // ملف ZIP: بطاقة لكل ملف استُخرج منه، وملخص لما تُجوهل.
        const cards = res.items.map(i => {
          const c = uploadCard(i.upload);
          if (i.duplicate) c.querySelector('.meta').append(' · رُفع سابقًا بالمحتوى نفسه');
          return c;
        });
        finish(card, chip, bar, res.skipped.length ? 'warn' : 'ok',
          `ZIP: ${fmt(res.items.length)} ملف${res.skipped.length ? ` · تُجوهل ${fmt(res.skipped.length)}` : ''}`);
        if (res.skipped.length) meta.append(' · ', el('span', { textContent: res.skipped.map(s => `${s.entry} (${s.message})`).join('، ') }));
        card.after(...cards);
        continue;
      }
      const replacement = uploadCard(res.upload);
      if (res.duplicate) replacement.querySelector('.meta').append(' · رُفع سابقًا بالمحتوى نفسه');
      card.replaceWith(replacement);
    } catch (e) {
      finish(card, chip, bar, 'bad', e.message);
    }
  }
}

function finish(card, chip, bar, cls, text) {
  chip.className = 'chip ' + cls;
  chip.textContent = text;
  bar.remove();
}

function uploadOne(file, path, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/uploads');
    xhr.withCredentials = true;
    xhr.setRequestHeader('X-QB-Client', 'web');
    xhr.upload.onprogress = e => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      let data = null;
      try { data = JSON.parse(xhr.responseText); } catch { /* ignore */ }
      if (xhr.status >= 200 && xhr.status < 300) resolve(data);
      else reject(new Error(data?.message || 'تعذّر رفع الملف.'));
    };
    xhr.onerror = () => reject(new Error('انقطع الاتصال أثناء الرفع.'));
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('path', path);
    xhr.send(form);
  });
}

/* ---------- review ---------- */
const LETTERS = ['أ', 'ب', 'ج', 'د', 'هـ', 'و', 'ز', 'ح'];
const RV_FILTERS = [['NeedsReview', 'تحتاج مراجعة'], ['AwaitingApproval', 'بانتظار الاعتماد'], ['Flagged', 'معلّمة للجنة'], ['Approved', 'معتمدة'], ['', 'الكل']];
const RV_STATUS_AR = { NeedsReview: 'تحتاج مراجعة', AwaitingApproval: 'بانتظار الاعتماد', Flagged: 'معلّمة للجنة', Approved: 'معتمدة', Superseded: 'مُستبدَلة بعد تغيّر الملف' };
const DOT = { High: 'g', Medium: 'y', Low: 'r' };
let rv = { filter: 'NeedsReview', items: [], total: 0, index: 0, counts: {} };

async function loadReview(reset) {
  if (reset) { rv.items = []; rv.index = 0; }
  let page;
  try { page = await api('GET', `/api/review?status=${rv.filter}&skip=${rv.items.length}&take=100`); } catch { return; }
  rv.items = rv.items.concat(page.items);
  rv.total = page.total;
  rv.counts = page.counts;
  renderReview();
}

async function reloadKeepingPosition() {
  const keepId = rv.items[rv.index]?.id;
  const oldIndex = rv.index;
  rv.items = [];
  await loadReview(false);
  const found = rv.items.findIndex(q => q.id === keepId);
  rv.index = found >= 0 ? found : Math.min(oldIndex, Math.max(0, rv.items.length - 1));
  renderReview();
}

function renderReview() {
  const counts = rv.counts;
  $('#n-review').textContent = fmt((counts.NeedsReview || 0) + (counts.AwaitingApproval || 0));
  const filters = $('#rv-filters');
  filters.replaceChildren(...RV_FILTERS.map(([key, label]) => {
    const n = key ? (counts[key] || 0) : Object.values(counts).reduce((a, b) => a + b, 0);
    const b = el('button', { className: 'btn sm' + (rv.filter === key ? ' on' : ''), type: 'button', textContent: `${label} ${fmt(n)}` });
    b.onclick = () => { rv.filter = key; loadReview(true); };
    return b;
  }));

  const list = $('#rv-list');
  list.replaceChildren(...rv.items.map((q, i) => {
    const row = el('button', { className: 'qrow', type: 'button', role: 'option' },
      el('span', { className: 'dot ' + DOT[q.confidence] }),
      el('span', {}, el('span', { className: 't', textContent: q.text }), el('span', { className: 'm', textContent: q.source })));
    row.setAttribute('aria-selected', String(i === rv.index));
    row.onclick = () => { rv.index = i; renderReview(); };
    return row;
  }));
  if (!rv.items.length) list.append(el('div', { className: 'empty', textContent: 'لا توجد أسئلة هنا.' }));
  $('#rv-more').hidden = rv.items.length >= rv.total;
  list.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
  renderDetail(rv.items[rv.index]);
}
$('#rv-more').onclick = () => loadReview(false);

function renderDetail(q) {
  const box = $('#rv-detail');
  box.replaceChildren();
  if (!q) { box.append(el('div', { className: 'muted', textContent: 'انتهت المراجعة في هذه القائمة.' })); return; }
  const locked = q.reviewStatus === 'Approved' || q.reviewStatus === 'Superseded';
  const CONF_AR = { High: 'ثقة عالية', Medium: 'ثقة متوسطة', Low: 'تحتاج تدخلًا' };

  box.append(el('div', { className: 'qstatus' },
    el('span', { className: 'mono muted', textContent: q.code || ('#' + q.id) }),
    el('span', { className: 'chip ' + (q.reviewStatus === 'Approved' ? 'ok' : q.reviewStatus === 'Flagged' ? 'warn' : 'wait'), textContent: RV_STATUS_AR[q.reviewStatus] || q.reviewStatus })));
  box.append(el('h2', { textContent: q.text, style: 'white-space:pre-wrap;font-size:18px' }));

  const opts = el('div', { className: 'opts' });
  q.options.forEach((o, i) => {
    const b = el('button', { className: 'opt' + (i === q.effectiveCorrectIndex ? ' correct' : ''), type: 'button', disabled: locked },
      el('span', { className: 'L', textContent: LETTERS[i] || String(i + 1) }), el('span', { textContent: o }));
    if (q.correctedIndex !== null && i === q.originalCorrectIndex) b.append(el('span', { className: 'orig', textContent: 'الإجابة في الملف' }));
    b.onclick = () => setAnswer(q, i);
    opts.append(b);
  });
  box.append(opts);

  const issues = [...q.issues];
  if (q.correctedIndex !== null) issues.unshift(`صُحّحت الإجابة يدويًا${q.reviewedBy ? ' بواسطة ' + q.reviewedBy : ''}.`);
  if (q.textCorrected) issues.unshift('صُحّح النص يدويًا؛ النص الأصلي محفوظ ويظهر أدناه.');
  if (!issues.length) box.append(el('div', { className: 'issue' }, el('span', { className: 'dot g' }), 'البنية سليمة: نص وخيارات وإجابة واحدة'));
  issues.forEach(t => box.append(el('div', { className: 'issue' }, el('span', { className: 'dot ' + (q.effectiveCorrectIndex === null ? 'r' : 'y') }), t)));
  box.append(el('div', { className: 'srcline', textContent: `${q.source} · ${q.location} · ${CONF_AR[q.confidence]} · طريقة كشف الإجابة: ${q.detector}` }));
  if (q.textCorrected) {
    const orig = el('details', { className: 'srcline' }, el('summary', { textContent: 'النص كما ورد في الملف' }),
      el('div', { textContent: q.originalText, style: 'white-space:pre-wrap;margin-top:4px' }),
      el('ol', {}, ...q.originalOptions.map(o => el('li', { textContent: o }))));
    box.append(orig);
  }

  const actions = el('div', { className: 'actions', style: 'margin-top:14px' });
  if (q.reviewStatus === 'Approved' || q.reviewStatus === 'Flagged') {
    actions.append(button('إعادة الفتح', () => act(q, 'reopen', 'أُعيد فتح السؤال.')));
  }
  if (!locked) {
    actions.append(button('اعتماد', () => act(q, 'approve', 'اعتُمد السؤال.', () => act(q, 'reopen', null)), 'primary', 'A'));
    if (q.reviewStatus !== 'Flagged') actions.append(button('تعليم للجنة', () => act(q, 'flag', 'عُلّم السؤال للجنة.', () => act(q, 'reopen', null)), '', 'F'));
    if (q.correctedIndex !== null) actions.append(button('إلغاء تصحيح الإجابة', () => setAnswer(q, null)));
    actions.append(button('تعديل النص', () => editText(q)));
    if (q.textCorrected) actions.append(button('إعادة النص الأصلي', () => saveText(q, null, null)));
  }
  actions.append(button('التالي', () => moveReview(1), '', 'J'));
  box.append(actions);
}

/* تعديل النص والخيارات كطبقة فوق الأصل: عدد الخيارات ثابت لأن الإجابة مرتبطة بموقعها. */
function editText(q) {
  const box = $('#rv-detail');
  box.replaceChildren(el('h2', { textContent: 'تعديل النص', style: 'font-size:16px' }),
    el('p', { className: 'note', textContent: 'صحّح أخطاء الاستخراج فقط (كلمة مكسورة، حرف ناقص). النص الأصلي يبقى محفوظًا، والتصدير يستخدم النص المصحّح.' }));
  const stem = el('textarea', { id: 'edit-stem', value: q.text, rows: 3, style: 'width:100%;font:inherit;padding:8px;border:1px solid var(--line);border-radius:8px;background:var(--surface);color:var(--fg)' });
  box.append(el('label', { className: 'field', style: 'display:block' }, 'نص السؤال:', stem));
  const inputs = q.options.map((o, i) => {
    const input = el('input', { id: `edit-opt-${i}`, value: o, style: 'flex:1;min-width:0' });
    box.append(el('label', { className: 'field' }, el('span', { className: 'mono', textContent: (LETTERS[i] || i + 1) + ')' }), input));
    return input;
  });
  const save = el('button', { className: 'btn primary', type: 'button', textContent: 'حفظ التصحيح' });
  save.onclick = () => saveText(q, stem.value, inputs.map(x => x.value));
  const cancel = el('button', { className: 'btn', type: 'button', textContent: 'إلغاء' });
  cancel.onclick = () => renderDetail(q);
  box.append(el('div', { className: 'actions', style: 'margin-top:12px' }, save, cancel));
  stem.focus();
}

async function saveText(q, text, options) {
  const before = q.textCorrected ? { text: q.text, options: q.options } : { text: null, options: null };
  try {
    await api('POST', `/api/review/${q.id}/text`, { text, options });
    toast(text === null ? 'أُعيد النص الأصلي.' : 'حُفظ تصحيح النص.',
      () => api('POST', `/api/review/${q.id}/text`, before).then(reloadKeepingPosition));
    await reloadKeepingPosition();
  } catch (e) { toast(e.message); }
}

function button(text, onclick, cls = '', key = '') {
  const b = el('button', { className: 'btn ' + cls, type: 'button' }, text);
  if (key) b.append(' ', el('kbd', { textContent: key }));
  b.onclick = onclick;
  return b;
}

async function setAnswer(q, index) {
  const previous = q.correctedIndex;
  try {
    await api('POST', `/api/review/${q.id}/answer`, { index });
    toast(index === null ? 'أُلغي التصحيح وعادت إجابة الملف.' : `عُيّن الخيار «${LETTERS[index]}» إجابة صحيحة.`,
      () => api('POST', `/api/review/${q.id}/answer`, { index: previous }).then(reloadKeepingPosition));
    await reloadKeepingPosition();
  } catch (e) { toast(e.message); }
}

async function act(q, action, message, undo) {
  try {
    await api('POST', `/api/review/${q.id}/${action}`);
    if (message) toast(message, undo);
    await reloadKeepingPosition();
  } catch (e) { toast(e.message); }
}

function moveReview(delta) {
  if (!rv.items.length) return;
  rv.index = (rv.index + delta + rv.items.length) % rv.items.length;
  renderReview();
}

document.addEventListener('keydown', e => {
  if ($('[data-screen="review"]').hidden || e.ctrlKey || e.metaKey || e.altKey || /INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) return;
  const q = rv.items[rv.index];
  const key = e.key.toLowerCase();
  if (key === 'j') moveReview(1);
  else if (key === 'k') moveReview(-1);
  else if (key === 'a' && q && q.reviewStatus !== 'Approved') act(q, 'approve', 'اعتُمد السؤال.', () => act(q, 'reopen', null));
  else if (key === 'f' && q && q.reviewStatus !== 'Flagged') act(q, 'flag', 'عُلّم السؤال للجنة.', () => act(q, 'reopen', null));
});

/* ---------- export basket ---------- */
// السلة محفوظة في متصفح المستخدم فقط (راحة شخصية)، والملفات والسجل على الخادم.
const BASKET_KEY = 'qb-basket';
let basket = [];
let approvedCache = new Map();
try { basket = JSON.parse(localStorage.getItem(BASKET_KEY) || '[]').filter(Number.isInteger); } catch { basket = []; }
function saveBasket() {
  try { localStorage.setItem(BASKET_KEY, JSON.stringify(basket)); } catch { /* storage unavailable */ }
  $('#n-basket').textContent = basket.length;
}
saveBasket();

async function loadExport() {
  let page;
  try { page = await api('GET', '/api/review?status=Approved&take=200'); } catch { return; }
  approvedCache = new Map(page.items.map(q => [q.id, q]));
  basket = basket.filter(id => approvedCache.has(id));
  saveBasket();
  renderExport();
  loadExportHistory();
}

function renderExport() {
  const list = $('#ex-approved');
  const available = [...approvedCache.values()].filter(q => !basket.includes(q.id));
  list.replaceChildren(...available.map(q => {
    const add = el('button', { className: 'btn sm', type: 'button', textContent: 'أضف' });
    add.onclick = () => addToBasket(q.id);
    const card = el('div', { className: 'excard', draggable: true },
      el('span', {}, el('span', { className: 't', textContent: q.text }), el('br'), el('span', { className: 'm', textContent: q.source })), add);
    card.addEventListener('dragstart', e => { e.dataTransfer.setData('text/x-qb-question', String(q.id)); e.dataTransfer.effectAllowed = 'copy'; });
    return card;
  }));
  if (!available.length) list.append(el('div', { className: 'empty', textContent: approvedCache.size ? 'كل الأسئلة المعتمدة في السلة.' : 'لا توجد أسئلة معتمدة بعد. اعتمدها من شاشة المراجعة.' }));

  const box = $('#ex-basket');
  box.replaceChildren(...basket.map(id => {
    const q = approvedCache.get(id);
    const rm = el('button', { className: 'btn sm', type: 'button', textContent: '×' });
    rm.setAttribute('aria-label', 'إزالة من السلة');
    rm.onclick = () => { basket = basket.filter(x => x !== id); saveBasket(); renderExport(); };
    return el('div', { className: 'bitem' }, el('span', { textContent: q ? q.text : '#' + id }), rm);
  }));
  if (!basket.length) box.append(el('div', { className: 'empty', textContent: 'أفلت الأسئلة هنا' }));
}

function addToBasket(id) {
  if (!basket.includes(id)) { basket.push(id); saveBasket(); renderExport(); }
}

const basketBox = $('#ex-basket');
['dragenter', 'dragover'].forEach(t => basketBox.addEventListener(t, e => {
  if (!e.dataTransfer.types.includes('text/x-qb-question')) return;
  e.preventDefault(); basketBox.classList.add('over');
}));
['dragleave', 'drop'].forEach(t => basketBox.addEventListener(t, () => basketBox.classList.remove('over')));
basketBox.addEventListener('drop', e => {
  const id = Number(e.dataTransfer.getData('text/x-qb-question'));
  if (id) { e.preventDefault(); addToBasket(id); }
});
$('#ex-add-all').onclick = () => { approvedCache.forEach((_, id) => { if (!basket.includes(id)) basket.push(id); }); saveBasket(); renderExport(); };
$('#ex-clear').onclick = () => {
  const previous = [...basket];
  basket = []; saveBasket(); renderExport();
  toast('فُرّغت السلة.', () => { basket = previous; saveBasket(); renderExport(); });
};
$('#ex-gift').onclick = () => runExport('Gift');
$('#ex-xml').onclick = () => runExport('MoodleXml');

async function runExport(format) {
  const box = $('#ex-result');
  box.replaceChildren();
  if (!basket.length) { toast('السلة فارغة.'); return; }
  try {
    const r = await api('POST', '/api/exports', {
      format, questionIds: basket, allowShuffle: $('#ex-shuffle').checked, categoryBySource: $('#ex-cat').checked
    });
    const link = el('a', { href: `/api/exports/${r.exportId}/file`, textContent: 'تنزيل الملف' });
    // bdi: أسماء الملفات والحسابات لاتينية، فنعزل اتجاهها حتى لا تنقلب داخل النص العربي.
    const res = el('div', { className: 'result' }, `صُدّر ${fmt(r.exported)} سؤال إلى `, el('bdi', { textContent: r.fileName }), '. ', link);
    if (r.rejected.length) res.append(el('div', { textContent: `استُبعد ${fmt(r.rejected.length)}: ` + r.rejected.map(x => `#${x.questionId} (${x.reason})`).join('، ') }));
    box.append(res);
    loadExportHistory();
  } catch (e) {
    box.append(el('div', { className: 'result bad', textContent: e.message }));
  }
}

async function loadExportHistory() {
  let items;
  try { items = await api('GET', '/api/exports'); } catch { return; }
  const box = $('#ex-history');
  if (!items.length) { box.className = 'muted'; box.textContent = 'لا توجد بعد.'; return; }
  box.className = 'hist';
  box.replaceChildren();
  for (const x of items) {
    box.append(
      el('span', { className: 'mono', textContent: '#' + x.id }),
      el('span', { textContent: x.format === 'Gift' ? 'GIFT' : 'Moodle XML' }),
      el('span', { textContent: `${fmt(x.questionCount)} سؤال` }),
      el('span', { className: 'muted' }, el('bdi', { textContent: x.requestedBy }), ' · ', el('bdi', { textContent: utc(x.atUtc) })),
      el('a', { href: `/api/exports/${x.id}/file`, textContent: 'تنزيل' }));
  }
}

/* ---------- duplicates ---------- */
let dupKind = 'Exact';
async function loadDups() {
  let page;
  try { page = await api('GET', '/api/quality/duplicates?kind=' + dupKind); } catch { return; }
  $('#n-dups').textContent = fmt(page.exactGroups);
  const filters = [['Exact', 'تكرار حرفي', page.exactGroups], ['SameStem', 'النص نفسه بخيارات مختلفة', page.sameStemGroups], ['Near', 'شبه تكرار (صياغة مختلفة قليلًا)', page.nearGroups]];
  $('#dup-filters').replaceChildren(...filters.map(([k, label, n]) => {
    const b = el('button', { className: 'btn sm' + (dupKind === k ? ' on' : ''), type: 'button', textContent: n == null ? label : `${label} ${fmt(n)}` });
    b.onclick = () => { dupKind = k; loadDups(); };
    return b;
  }));
  const box = $('#dup-groups');
  box.replaceChildren();
  if (!page.groups.length) { box.append(el('div', { className: 'panel muted', textContent: 'لا توجد مجموعات في هذه الفئة.' })); return; }
  page.groups.forEach((g, gi) => {
    const members = el('div', { className: 'dmembers' }, ...g.members.map(m => {
      const ol = el('ol');
      m.options.forEach((o, i) => ol.append(el('li', { className: i === m.effectiveCorrectIndex ? 'correct' : '', textContent: o })));
      return el('div', { className: 'dm' },
        el('div', { className: 't', textContent: m.text }), ol,
        el('div', { className: 'm' }, el('bdi', { textContent: m.source }), ` · ${m.location} · ${RV_STATUS_AR[m.reviewStatus] || m.reviewStatus}`));
    }));
    const title = `مجموعة ${fmt(gi + 1)} · ${fmt(g.count)} ${dupKind === 'Near' ? 'أسئلة' : 'نسخ'}` + (g.similarity != null ? ` · تشابه ${Math.round(g.similarity * 100)}% على الأقل` : '');
    const head = el('div', { className: 'dh' }, el('h2', { textContent: title, style: 'margin:0' }));
    if (g.answersDiffer) head.append(el('span', { className: 'chip bad', textContent: 'الإجابات مختلفة بين النسخ' }));
    box.append(el('div', { className: 'panel dgroup' }, head, members));
  });
}

/* ---------- classification ---------- */
let fwId = null;
let selectedQuestion = null;

async function loadFrameworks() {
  let list;
  try { list = await api('GET', '/api/frameworks'); } catch { return; }
  const sel = $('#fw-select');
  sel.replaceChildren(...list.map(f => el('option', { value: f.id, textContent: `${f.name} · ${f.version} (${fmt(f.indicatorCount)} مؤشر)` })));
  if (!list.length) {
    $('#fw-tree').replaceChildren(el('div', { className: 'muted', textContent: 'لا يوجد إطار مستورد بعد. استورد ملف الإطار من الأعلى.' }));
    $('#cl-questions').replaceChildren();
    $('#fw-stats').textContent = '';
    return;
  }
  if (!list.some(f => f.id === fwId)) fwId = list[0].id;
  sel.value = fwId;
  await loadClassify();
}
$('#fw-select').onchange = e => { fwId = Number(e.target.value); loadClassify(); };

async function loadClassify() {
  let tree, items;
  try {
    [tree, items] = await Promise.all([api('GET', `/api/frameworks/${fwId}/tree`), api('GET', `/api/frameworks/${fwId}/unclassified?take=100`)]);
  } catch { return; }
  $('#fw-title').textContent = `${tree.framework.name} · إصدار ${tree.framework.version}`;
  $('#fw-stats').textContent = `${fmt(tree.classifiedQuestions)} سؤال مصنّف · ${fmt(tree.indicatorsWithoutQuestions)} مؤشر بلا أسئلة`;

  const qbox = $('#cl-questions');
  qbox.replaceChildren(...items.map(q => {
    const card = el('div', { className: 'qcard' + (selectedQuestion === q.id ? ' sel' : ''), draggable: true },
      el('span', { className: 't', textContent: q.text }), el('span', { className: 'm' }, el('bdi', { textContent: q.source })));
    const sugg = el('div', { className: 'sugg' }, el('span', { className: 'm', textContent: q.suggestions.length ? 'اقتراح نصي:' : 'لا اقتراح' }));
    q.suggestions.forEach(s => {
      const b = el('button', { className: 'sbtn', type: 'button', title: `${s.name}\n${s.rationale}` },
        el('span', { className: 'mono', textContent: s.code || '—' }), el('span', { className: 'muted', textContent: Math.round(s.score * 100) + '%' }));
      b.onclick = e => { e.stopPropagation(); classify(q.id, s.indicatorId, s.method, s.score); };
      sugg.append(b);
    });
    card.append(sugg);
    card.addEventListener('dragstart', e => e.dataTransfer.setData('text/x-qb-classify', String(q.id)));
    card.onclick = () => { selectedQuestion = selectedQuestion === q.id ? null : q.id; loadClassify(); };
    return card;
  }));
  if (!items.length) qbox.append(el('div', { className: 'empty', textContent: 'كل الأسئلة مصنّفة.' }));

  const tbox = $('#fw-tree');
  tbox.replaceChildren();
  for (const d of tree.domains) {
    tbox.append(el('div', { className: 'dom', textContent: d.name }));
    for (const s of d.subs) {
      if (s.name) tbox.append(el('div', { className: 'subd', textContent: s.name }));
      for (const i of s.indicators) {
        const row = el('div', { className: 'ind' },
          el('span', {}, i.code ? el('span', { className: 'code', textContent: i.code + ' ' }) : '', i.name),
          el('span', { className: 'cnt' + (i.questions ? '' : ' zero'), title: i.questions ? '' : 'مؤشر بلا أسئلة', textContent: fmt(i.questions) }));
        ['dragenter', 'dragover'].forEach(t => row.addEventListener(t, e => { if (e.dataTransfer.types.includes('text/x-qb-classify')) { e.preventDefault(); row.classList.add('over'); } }));
        row.addEventListener('dragleave', () => row.classList.remove('over'));
        row.addEventListener('drop', e => { e.preventDefault(); row.classList.remove('over'); classify(Number(e.dataTransfer.getData('text/x-qb-classify')), i.id, 'manual', null); });
        row.onclick = () => { if (selectedQuestion) { const q = selectedQuestion; selectedQuestion = null; classify(q, i.id, 'manual', null); } };
        tbox.append(row);
      }
    }
  }
}

async function classify(questionId, indicatorId, method, score) {
  try {
    await api('POST', `/api/classify/${questionId}`, { indicatorId, method, score });
    toast('رُبط السؤال بالمؤشر.', () => api('POST', `/api/classify/${questionId}`, { indicatorId: null }).then(loadClassify));
    await loadClassify();
  } catch (e) { toast(e.message); }
}

$('#fw-import').addEventListener('submit', async e => {
  e.preventDefault();
  const box = $('#fw-import-result');
  box.replaceChildren();
  const form = new FormData();
  form.append('file', $('#fw-file').files[0]);
  form.append('name', $('#fw-name').value);
  form.append('version', $('#fw-version').value);
  const res = await fetch('/api/frameworks', { method: 'POST', body: form, credentials: 'same-origin', headers: { 'X-QB-Client': 'web' } });
  let data = null;
  try { data = await res.json(); } catch { /* ignore */ }
  if (res.status === 403) { box.append(el('div', { className: 'result bad', textContent: 'الاستيراد لمدير النظام ومسؤول البنك فقط.' })); return; }
  if (!res.ok) {
    box.append(el('div', { className: 'result bad', textContent: (data?.errors || ['تعذّر الاستيراد.']).join(' · ') }));
    return;
  }
  box.append(el('div', { className: 'result', textContent: `استُورد ${fmt(data.framework.indicatorCount)} مؤشر.` + (data.warnings.length ? ' ملاحظات: ' + data.warnings.join(' · ') : '') }));
  fwId = data.framework.id;
  loadFrameworks();
});

/* ---------- start ---------- */
loadMe();
loadQueue();
show(['dash', 'folders', 'drop', 'review', 'export', 'dups', 'classify'].includes(location.hash.slice(1)) ? location.hash.slice(1) : 'dash');
