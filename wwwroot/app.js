'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const contentEl = $('#content');
const chipsEl = $('#monitorChips');
const dotEl = $('#connDot');
const toastEl = $('#toast');
const tokenModal = $('#tokenModal');

let TOKEN = localStorage.getItem('benq.token') || '';

// The pairing QR code opens "/#t=<token>". Store it, then drop it from the address bar so it
// does not linger in history or screenshots. A fragment never reaches the server.
const pairedToken = new URLSearchParams(location.hash.slice(1)).get('t');
if (pairedToken) {
  TOKEN = pairedToken;
  localStorage.setItem('benq.token', pairedToken);
  history.replaceState(null, '', location.pathname + location.search);
}
let monitors = [];
let currentId = localStorage.getItem('benq.monitor') || null;
let ws = null;
let wsTimer = null;

/** code -> { feature, root, setValue(feature) } */
const nodes = new Map();
const activeCodes = new Set();
/** "monitorId|code" -> { monitorId, code, value } — newest pending write per control. */
const desired = new Map();
const sending = new Set();

/** Monitor currently rendered on screen; writes are attributed to it at queue time. */
let renderedMonitorId = null;
const BRIGHTNESS_CODE = 0x10;
const LOW_BLUE_LIGHT_CODE = 0x19;
/** Brightness, input source, picture mode, low blue light. */
const QUICK_CODES = [BRIGHTNESS_CODE, 0x60, 0xDC, LOW_BLUE_LIGHT_CODE];

/** Shape of the last chip row, so polling does not rebuild it needlessly. */
let chipsSignature = '';
/** True once the user taps a chip — stops automatic re-selection from overriding them. */
let pinnedByUser = false;

/* ------------------------------------------------------------------ api */

async function api(path, options = {}) {
  const headers = Object.assign({}, options.headers);
  if (TOKEN) headers['X-Api-Token'] = TOKEN;
  if (options.body) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, Object.assign({}, options, { headers }));

  if (response.status === 401) {
    showTokenModal('That token was rejected. Check the console window.');
    throw new Error('unauthorized');
  }

  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }

  if (!response.ok) {
    const message = (data && data.error) ? data.error : `HTTP ${response.status}`;
    throw new Error(message);
  }

  return data;
}

/* ------------------------------------------------------------------ ui bits */

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

let toastTimer = null;
function toast(message, isError = false) {
  toastEl.textContent = message;
  toastEl.className = 'toast show' + (isError ? ' err' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toastEl.className = 'toast'; }, 2600);
}

function setConnected(connected) {
  dotEl.className = 'dot ' + (connected ? 'live' : 'down');
}

/* ------------------------------------------------------------------ token */

function showTokenModal(hint) {
  tokenModal.hidden = false;
  $('#tokenHint').textContent = hint || '';
  const input = $('#tokenInput');
  input.value = TOKEN;
  setTimeout(() => input.focus(), 50);
}

$('#tokenSave').addEventListener('click', () => {
  const value = $('#tokenInput').value.trim();
  if (!value) return;
  TOKEN = value;
  localStorage.setItem('benq.token', value);
  tokenModal.hidden = true;
  bootstrap();
});

$('#tokenInput').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') $('#tokenSave').click();
});

/* ------------------------------------------------------------------ data flow */

async function bootstrap() {
  if (!TOKEN) { showTokenModal(); return; }

  try {
    const list = await api('/api/monitors');
    if (!list || !list.length) retryEmptyList();
    applyMonitors(list, true);
    connectWs();
  } catch (error) {
    if (error.message !== 'unauthorized') {
      contentEl.innerHTML = '';
      contentEl.appendChild(el('div', 'placeholder', 'Could not reach the bridge: ' + error.message));
      setConnected(false);
    }
  }
}

function applyMonitors(list, force) {
  const before = monitors;
  monitors = list;

  if (!monitors.length) {
    contentEl.innerHTML = '';
    contentEl.appendChild(el('div', 'placeholder', 'No displays detected.'));
    chipsEl.innerHTML = '';
    chipsSignature = '';
    renderedMonitorId = null;
    return;
  }

  const usable = monitors.find((m) => m.supported && m.features.length > 0);
  const current = monitors.find((m) => m.id === currentId);
  const currentUsable = current && current.supported && current.features.length > 0;

  // Prefer a display that actually answered the capability request. Without this the
  // first (usually laptop) panel wins and the UI looks empty even though a real monitor
  // is present. A deliberate chip tap is never overridden.
  if (!current || (!pinnedByUser && !currentUsable && usable)) {
    const chosen = usable
      || monitors.find((m) => m.supported)
      || monitors.find((m) => m.ddcChannel)
      || monitors[0];
    currentId = chosen.id;
    localStorage.setItem('benq.monitor', currentId);
  }

  renderChips();

  const monitor = monitors.find((m) => m.id === currentId);
  const sameShape = !force && shapeOf(before.find((m) => m.id === currentId)) === shapeOf(monitor);

  if (sameShape) applyState(monitor);
  else renderMonitor(monitor);
}

function shapeOf(monitor) {
  if (!monitor) return '';
  const features = monitor.features
    .map((f) => f.hex + ':' + (f.options ? f.options.length : 0))
    .join(',');
  return monitor.id + '|' + monitor.supported + '|' + features;
}

function renderChips() {
  const signature = currentId + '\u0000' + monitors
    .map((m) => m.id + ':' + (m.name || '') + ':' + (m.supported ? 1 : 0) + ':' + m.features.length)
    .join('|');

  if (signature === chipsSignature) return;
  chipsSignature = signature;

  chipsEl.innerHTML = '';
  for (const monitor of monitors) {
    const classes = ['chip'];
    if (monitor.id === currentId) classes.push('active');
    if (!monitor.supported) classes.push('unsupported');

    const chip = el('button', classes.join(' '));
    chip.textContent = monitor.name || monitor.id;
    if (!monitor.supported) {
      chip.title = monitor.ddcChannel
        ? 'No features reported by this display yet'
        : 'This display has no DDC/CI channel';
    }

    chip.addEventListener('click', () => {
      currentId = monitor.id;
      pinnedByUser = true;
      localStorage.setItem('benq.monitor', currentId);
      chipsSignature = '';
      renderChips();
      renderMonitor(monitors.find((m) => m.id === currentId));
    });

    chipsEl.appendChild(chip);
  }
}

/* ------------------------------------------------------------------ rendering */

function renderMonitor(monitor) {
  contentEl.innerHTML = '';
  nodes.clear();
  activeCodes.clear();
  renderedMonitorId = monitor ? monitor.id : null;

  if (!monitor) return;

  if (!monitor.supported) {
    const note = el('div', 'note');
    if (monitor.ddcChannel) {
      note.textContent = 'This display answered enumeration but has not returned a capability '
        + 'string yet. It is retried automatically every 10 seconds, or press the refresh button to retry now.';
    } else {
      note.textContent = 'This display does not expose a DDC/CI channel, so Windows cannot '
        + 'control it. Typical for laptop internal panels and some USB-C docks.';
    }
    contentEl.appendChild(note);
    return;
  }

  if (!monitor.features.length) {
    const note = el('div', 'note');
    note.textContent = 'The monitor did not advertise any VCP features. Press the refresh button to re-read it.';
    contentEl.appendChild(note);
    return;
  }

  const order = monitor.categories && monitor.categories.length
    ? monitor.categories
    : ['Picture', 'Color'];

  // One-shot commands (factory reset etc.) are pulled out of their categories and given
  // their own danger zone at the bottom, so a stray tap cannot wipe the monitor.
  const actions = monitor.features.filter((f) => f.kind === 'action');

  // The few controls people reach for daily sit in a block at the top.
  const quick = QUICK_CODES
    .map((code) => monitor.features.find((f) => f.code === code && f.kind !== 'action'))
    .filter(Boolean);

  const groups = new Map();
  for (const feature of monitor.features) {
    if (feature.kind === 'action' || quick.includes(feature)) continue;
    if (!groups.has(feature.category)) groups.set(feature.category, []);
    groups.get(feature.category).push(feature);
  }

  const categories = [...groups.keys()].sort((a, b) => {
    const ia = order.indexOf(a); const ib = order.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
  });

  if (quick.length) contentEl.appendChild(makeGroup(quick, true));

  for (const category of categories) {
    if (category === 'Advanced') {
      const fold = el('details', 'fold');
      fold.appendChild(el('summary', null, 'Advanced / raw VCP codes (' + groups.get(category).length + ')'));
      fold.appendChild(makeGroup(groups.get(category)));
      contentEl.appendChild(fold);
      continue;
    }

    contentEl.appendChild(el('div', 'section-title', category));
    contentEl.appendChild(makeGroup(groups.get(category)));
  }

  if (actions.length) {
    const fold = el('details', 'fold danger-fold');
    fold.appendChild(el('summary', null, 'Reset to factory settings'));
    fold.appendChild(el('div', 'note reset-intro',
      'These send a one-shot command to the monitor itself. The factory values live in the '
      + 'monitor firmware and cannot be read back over DDC, so a reset cannot be undone.'));

    const group = el('section', 'group');
    for (const feature of actions) group.appendChild(makeResetCard(feature));
    fold.appendChild(group);
    contentEl.appendChild(fold);
  }

  applyState(monitor);
}

/** One surface per category; the daily controls get a larger brightness readout. */
function makeGroup(features, hero = false) {
  const group = el('section', hero ? 'group hero' : 'group');
  for (const feature of features) {
    const big = hero && feature.kind === 'continuous' && feature.code === BRIGHTNESS_CODE;
    group.appendChild(big ? makeContinuous(feature, true) : makeCard(feature));
  }
  return group;
}

function makeCard(feature) {
  switch (feature.kind) {
    case 'continuous': return makeContinuous(feature);
    case 'discrete':
    case 'toggle': return makeChoice(feature);
    case 'action': return makeResetCard(feature);
    default: return makeInfo(feature);
  }
}

/** Paints the filled part of the slider track. */
function paintFill(input) {
  const max = Number(input.max) || 100;
  input.style.setProperty('--pct', (Number(input.value) / max * 100) + '%');
}

/** True when a touch at clientX lands on the slider handle (with some room for a fat finger). */
function isOnHandle(input, clientX) {
  const handle = input.closest('.hero-card') ? 36 : 28;
  const rect = input.getBoundingClientRect();
  const max = Number(input.max) || 100;
  const centre = rect.left + handle / 2 + (Number(input.value) / max) * (rect.width - handle);
  return Math.abs(clientX - centre) <= handle / 2 + 14;
}

function makeContinuous(feature, big = false) {
  const code = feature.code;
  const card = el('div', big ? 'card hero-card' : 'card');
  const head = el('div', 'card-head');
  head.appendChild(el('span', 'card-name', feature.name));
  const valueEl = el('span', 'card-value');
  valueEl.innerHTML = '<b>—</b>';
  head.appendChild(valueEl);
  card.appendChild(head);

  const input = document.createElement('input');
  input.type = 'range';
  input.min = '0';
  input.max = String(feature.max > 0 ? feature.max : 100);
  input.step = '1';
  input.value = String(feature.value ?? 0);
  input.dataset.code = String(code);
  input.setAttribute('aria-label', feature.name);
  paintFill(input);
  card.appendChild(input);

  // Scroll swipes often start on a slider. A touch only counts if it grabs the handle;
  // anywhere else on the track the page scrolls and the value snaps back untouched.
  let accepted = Number(input.value);
  let grabbed = true;

  const release = () => {
    activeCodes.delete(code);
    grabbed = true;
  };

  input.addEventListener('pointerdown', (event) => {
    grabbed = event.pointerType !== 'touch' || isOnHandle(input, event.clientX);
    if (grabbed) activeCodes.add(code);
  });
  input.addEventListener('pointerup', release);
  input.addEventListener('pointercancel', release);
  input.addEventListener('change', release);

  input.addEventListener('input', () => {
    if (!grabbed) {
      input.value = String(accepted);
      paintFill(input);
      return;
    }

    accepted = Number(input.value);
    activeCodes.add(code);
    paintFill(input);
    updateContinuousLabel(valueEl, feature, accepted);
    queueWrite(code, accepted);
  });

  nodes.set(code, {
    feature,
    setValue(f) {
      updateContinuousLabel(valueEl, f, f.value);
      if (activeCodes.has(code)) return;
      input.max = String(f.max > 0 ? f.max : input.max);
      if (f.value !== null && f.value !== undefined) input.value = String(f.value);
      accepted = Number(input.value);
      paintFill(input);
    }
  });

  return card;
}

function updateContinuousLabel(target, feature, value) {
  const shown = (value === null || value === undefined) ? '—' : value;
  const unit = feature.unit ? feature.unit : '';
  target.innerHTML = '<b>' + shown + '</b>' + unit;
}

function makeChoice(feature) {
  const code = feature.code;
  const card = el('div', code === LOW_BLUE_LIGHT_CODE ? 'card eyecare' : 'card');
  const head = el('div', 'card-head');
  head.appendChild(el('span', 'card-name', feature.name));
  const valueEl = el('span', 'card-value', '');
  head.appendChild(valueEl);
  card.appendChild(head);

  const options = feature.options || [];
  const wrap = el('div', feature.kind === 'toggle' && options.length === 2 ? 'toggle' : 'seg');
  const buttons = new Map();

  for (const option of options) {
    const button = el('button', null, option.label);
    button.addEventListener('click', () => {
      for (const [value, node] of buttons) node.classList.toggle('active', value === option.value);
      valueEl.textContent = option.label;
      queueWrite(code, option.value);
    });
    buttons.set(option.value, button);
    wrap.appendChild(button);
  }

  card.appendChild(wrap);

  if (!options.length) {
    card.appendChild(el('div', 'note',
      'This control did not advertise its value list, so it can only be set through the raw API.'));
  }

  nodes.set(code, {
    feature,
    setValue(f) {
      if (!f.options) return;
      for (const [value, button] of buttons) {
        button.classList.toggle('active', value === f.value);
      }
      const current = f.options.find((o) => o.value === f.value);
      valueEl.textContent = current ? current.label : (f.value === null ? '' : String(f.value));
    }
  });

  return card;
}

const RESET_COPY = {
  4: {
    text: 'Restores every monitor setting to how it left the factory: picture mode, brightness, '
      + 'contrast, colour, low blue light, audio and OSD language.',
    warning: 'If the factory default input is not the one you are using, the picture will go '
      + 'black until you pick the input again with the monitor buttons.'
  },
  5: {
    text: 'Restores brightness and contrast to the factory defaults.',
    warning: ''
  },
  8: {
    text: 'Restores colour temperature and the red/green/blue gains to the factory defaults.',
    warning: ''
  }
};

const HOLD_MS = 1500;

function makeResetCard(feature) {
  const copy = RESET_COPY[feature.code] || {
    text: 'Sends a one-shot command to the monitor.',
    warning: ''
  };

  const card = el('div', 'card danger');
  const head = el('div', 'card-head');
  head.appendChild(el('span', 'card-name', feature.name));
  head.appendChild(el('span', 'card-value', 'VCP 0x' + feature.hex));
  card.appendChild(head);

  card.appendChild(el('p', 'danger-text', copy.text));
  if (copy.warning) card.appendChild(el('p', 'danger-text warn', copy.warning));

  const button = el('button', 'hold');
  const fill = el('span', 'hold-fill');
  const label = el('span', 'hold-label', 'Hold ' + (HOLD_MS / 1000) + 's to run');
  button.appendChild(fill);
  button.appendChild(label);
  card.appendChild(button);

  let timer = null;
  let done = false;

  const cancel = () => {
    if (timer) clearTimeout(timer);
    timer = null;
    button.classList.remove('holding');
  };

  const run = async () => {
    done = true;
    button.classList.remove('holding');
    button.classList.add('done');
    label.textContent = 'Sent';

    const targetId = renderedMonitorId;
    try {
      await api(`/api/monitors/${encodeURIComponent(targetId)}/vcp/${feature.hex}`, {
        method: 'PUT',
        body: JSON.stringify({ value: 1 })
      });

      toast(feature.name + ' sent — re-reading monitor…');
      rereadAfterReset(targetId);
    } catch (error) {
      toast(error.message, true);
      button.classList.remove('done');
      label.textContent = 'Hold ' + (HOLD_MS / 1000) + 's to run';
      done = false;
    }
  };

  button.addEventListener('pointerdown', (event) => {
    if (done) return;
    event.preventDefault();
    button.classList.add('holding');
    label.textContent = 'Keep holding…';
    timer = setTimeout(run, HOLD_MS);
  });

  button.addEventListener('pointerup', () => {
    cancel();
    if (!done) label.textContent = 'Hold ' + (HOLD_MS / 1000) + 's to run';
  });

  button.addEventListener('pointerleave', () => {
    cancel();
    if (!done) label.textContent = 'Hold ' + (HOLD_MS / 1000) + 's to run';
  });

  button.addEventListener('pointercancel', cancel);
  button.addEventListener('contextmenu', (event) => event.preventDefault());

  // The monitor drops DDC for a moment while it reinitialises, so re-read afterwards.
  nodes.set(feature.code, { feature, setValue() { } });
  return card;
}

/** Wait for the panel to finish resetting, then pull every value again. */
function rereadAfterReset(monitorId) {
  setTimeout(async () => {
    try {
      await api(`/api/monitors/${encodeURIComponent(monitorId)}/refresh`, { method: 'POST' });
      const list = await api('/api/monitors');
      applyMonitors(list, true);
      toast('Monitor re-read');
    } catch {
      // The poll loop will catch up on its own.
    }
  }, 4000);
}

function makeInfo(feature) {
  const card = el('div', 'card');
  const row = el('div', 'row');
  row.appendChild(el('span', 'k', feature.name + '  (0x' + feature.hex + ')'));
  const valueEl = el('span', 'v', '—');
  row.appendChild(valueEl);
  card.appendChild(row);

  nodes.set(feature.code, {
    feature,
    setValue(f) { valueEl.textContent = (f.value === null || f.value === undefined) ? '—' : String(f.value); }
  });

  return card;
}

function applyState(monitor) {
  if (!monitor) return;
  for (const feature of monitor.features) {
    const node = nodes.get(feature.code);
    if (!node) continue;
    node.feature = feature;
    try { node.setValue(feature); } catch { /* keep going */ }
  }
}

/* ------------------------------------------------------------------ writes */

function queueWrite(code, value) {
  if (!renderedMonitorId) return;
  const key = renderedMonitorId + '|' + code;
  desired.set(key, { monitorId: renderedMonitorId, code, value });
  pump(key);
}

/** Sends writes for one control strictly in order, always coalescing to the newest value. */
async function pump(key) {
  if (sending.has(key)) return;
  sending.add(key);

  try {
    while (desired.has(key)) {
      const job = desired.get(key);
      desired.delete(key);
      await sendWrite(job.monitorId, job.code, job.value);
      if (desired.has(key)) await new Promise((resolve) => setTimeout(resolve, 60));
    }
  } finally {
    sending.delete(key);
  }
}

async function sendWrite(monitorId, code, value) {
  if (!monitorId) return;
  const hex = code.toString(16).toUpperCase().padStart(2, '0');

  try {
    const result = await api(`/api/monitors/${encodeURIComponent(monitorId)}/vcp/${hex}`, {
      method: 'PUT',
      body: JSON.stringify({ value })
    });

    if (result && result.confirmed === false) {
      toast(`${result.name}: monitor reported ${result.value} instead of ${value}`, true);
    }
  } catch (error) {
    toast(error.message, true);
  }
}

/* ------------------------------------------------------------------ websocket */

function connectWs() {
  if (!TOKEN) return;

  clearTimeout(wsTimer);
  emptyRetries = 0;
  if (ws) {
    try { ws.onclose = null; ws.close(); } catch { /* ignored */ }
  }

  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${protocol}://${location.host}/ws?token=${encodeURIComponent(TOKEN)}`);
  ws = socket;

  socket.onopen = () => setConnected(true);

  socket.onmessage = (event) => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    if (message.type !== 'state') return;

    if (!message.monitors || !message.monitors.length) {
      applyMonitors([], false);
      retryEmptyList();
      return;
    }

    applyMonitors(message.monitors, false);
  };

  socket.onclose = () => {
    setConnected(false);
    wsTimer = setTimeout(connectWs, 2500);
  };

  socket.onerror = () => setConnected(false);
}

/**
 * The very first websocket snapshot can be taken before the server has finished its
 * initial display enumeration (enumerating a sleeping panel takes a second or two).
 * Keep asking until the list is non-empty instead of sitting on "no displays".
 */
let emptyRetryTimer = null;
let emptyRetries = 0;
const EMPTY_RETRY_LIMIT = 15;

function retryEmptyList() {
  if (emptyRetryTimer || emptyRetries >= EMPTY_RETRY_LIMIT) return;

  emptyRetryTimer = setTimeout(async () => {
    emptyRetryTimer = null;
    emptyRetries++;

    try {
      const list = await api('/api/monitors');
      if (list && list.length) {
        emptyRetries = 0;
        applyMonitors(list, true);
        return;
      }
    } catch { /* the socket will reconnect and try again */ }

    retryEmptyList();
  }, 800);
}

/* ------------------------------------------------------------------ toolbar */

$('#btnRefresh').addEventListener('click', async (event) => {
  const button = event.currentTarget;
  if (!currentId) return;

  button.classList.add('spin');
  try {
    await api(`/api/monitors/${encodeURIComponent(currentId)}/refresh`, { method: 'POST' });
    const list = await api('/api/monitors');
    applyMonitors(list, true);
    toast('Monitor re-read');
  } catch (error) {
    toast(error.message, true);
  } finally {
    setTimeout(() => button.classList.remove('spin'), 400);
  }
});

/* ------------------------------------------------------------------ start */

bootstrap();
