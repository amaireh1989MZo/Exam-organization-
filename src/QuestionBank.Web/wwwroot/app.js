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
    headers: body ? { 'Content-Type': 'application/json' } : {},
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
        el('span', { className: 'meta', textContent: `${fmt(q.fileCount)} ملف · ${q.requestedBy} · ${utc(q.requestedAtUtc)}` }),
        remove)));
  });
}

/* ---------- start ---------- */
loadMe();
loadQueue();
show(['dash', 'folders'].includes(location.hash.slice(1)) ? location.hash.slice(1) : 'dash');
