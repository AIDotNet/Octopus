// The `octopus` module: everything a plugin can reach of Octopus goes through here (design/plugins/02-运行时.md §3).
// The host loads it first, under the fixed id "octopus", in a Hardened engine. It captures the host's native
// functions and deletes them from globalThis, so plugin code can only use what this module exposes.
//
// Wire protocol (all payloads are JSON text; large text travels in the separate `text` argument):
//   plugin → host  __oct_post(kind, id, method, json, text)   kind: request | notify | result | error
//   host → plugin  dispatch(kind, id, name, json, text, fn)    kind: resolve | reject | event | invoke | timer | lifecycle

const native = {
  post: globalThis.__oct_post,
  sync: globalThis.__oct_sync,
  log: globalThis.__oct_log,
  now: globalThis.__oct_now,
  register: globalThis.__oct_register,
};
for (const name of ['__oct_post', '__oct_sync', '__oct_log', '__oct_now', '__oct_register']) delete globalThis[name];

// ───────────────────────── basics ─────────────────────────

export class OctopusError extends Error {
  constructor(code, message, details) {
    super(message);
    this.code = code;
    if (details) {
      if (details.path !== undefined) this.path = details.path;
      if (details.permission !== undefined) this.permission = details.permission;
      if (details.canRequest !== undefined) this.canRequest = details.canRequest;
      if (details.retryAfterMs !== undefined) this.retryAfterMs = details.retryAfterMs;
    }
  }
}

// Intrinsics are frozen (Hardened profile), so `this.name = …` on an Error would throw: define it on our own prototype.
Object.defineProperty(OctopusError.prototype, 'name', { value: 'OctopusError', writable: true, configurable: true });

const noop = () => {};
function disposable(fn) {
  let done = false;
  return { dispose() { if (!done) { done = true; fn(); } } };
}
const toJson = value => (value === undefined ? '' : JSON.stringify(value));
const fromJson = json => (json === '' || json === undefined ? undefined : JSON.parse(json));

let nextRequest = 1;
const pending = new Map();

/** Calls the host and waits for the answer (a later turn). `text` carries large strings outside the JSON. */
function request(method, args, text) {
  const id = nextRequest++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    native.post('request', id, method, toJson(args), text ?? '');
  });
}

/** Tells the host something without waiting. */
function notify(method, args, text) {
  native.post('notify', 0, method, toJson(args), text ?? '');
}

/** Reads a snapshot the host keeps in memory (never touches the disk). */
function sync(method, args) {
  return fromJson(native.sync(method, toJson(args)));
}

function cancellable(method, args, signal, text) {
  if (!signal) return request(method, args, text);
  if (signal.aborted) return Promise.reject(new OctopusError('Cancelled', 'The operation was cancelled.'));
  const id = nextRequest;
  const promise = request(method, args, text);
  signal.addEventListener('abort', () => notify('request.cancel', { id }));
  return promise;
}

// ───────────────────────── handlers the host calls ─────────────────────────

const handlers = new Map();

function handle(key, fn) {
  handlers.set(key, fn);
  return disposable(() => { if (handlers.get(key) === fn) handlers.delete(key); });
}

// ───────────────────────── events ─────────────────────────

const listeners = new Map();
let nextSubscription = 1;

/** Subscribes to a host event. The host starts sending it with the first listener and stops with the last. */
function onHost(event, args) {
  return listener => {
    const key = args === undefined ? event : `${event}#${nextSubscription++}`;
    let set = listeners.get(key);
    if (!set) {
      set = new Set();
      listeners.set(key, set);
      notify('events.subscribe', { event, key, args });
    }
    set.add(listener);
    return disposable(() => {
      set.delete(listener);
      if (set.size === 0) {
        listeners.delete(key);
        notify('events.unsubscribe', { key });
      }
    });
  };
}

function emitLocal(key, payload) {
  const set = listeners.get(key);
  if (!set) return;
  for (const listener of [...set]) {
    try {
      const result = listener(payload);
      if (result && typeof result.then === 'function') result.then(noop, reportError);
    } catch (e) {
      reportError(e);
    }
  }
}

function reportError(e) {
  native.log('error', String(e && e.stack ? e.stack : e));
}

// ───────────────────────── globals a plugin expects ─────────────────────────

function format(args) {
  return args.map(a => {
    if (typeof a === 'string') return a;
    if (a instanceof Error) return a.stack || String(a);
    try { return JSON.stringify(a, null, 0) ?? String(a); } catch { return String(a); }
  }).join(' ');
}

globalThis.console = {
  log: (...a) => native.log('info', format(a)),
  info: (...a) => native.log('info', format(a)),
  warn: (...a) => native.log('warn', format(a)),
  error: (...a) => native.log('error', format(a)),
  debug: (...a) => native.log('debug', format(a)),
};

const timers = new Map();
let nextTimer = 1;
function addTimer(fn, ms, args, repeat) {
  if (typeof fn !== 'function') throw new TypeError('callback must be a function');
  const id = nextTimer++;
  timers.set(id, { fn, args, repeat });
  notify('timer.set', { id, ms: Math.max(16, Number(ms) | 0), repeat });
  return id;
}
function clearTimer(id) {
  if (timers.delete(id)) notify('timer.clear', { id });
}
globalThis.setTimeout = (fn, ms, ...args) => addTimer(fn, ms, args, false);
globalThis.setInterval = (fn, ms, ...args) => addTimer(fn, ms, args, true);
globalThis.clearTimeout = clearTimer;
globalThis.clearInterval = clearTimer;
globalThis.queueMicrotask = fn => { Promise.resolve().then(fn).then(noop, reportError); };
globalThis.performance = { now: () => native.now() };

class AbortSignalImpl {
  constructor() { this.aborted = false; this.reason = undefined; this._listeners = []; }
  addEventListener(type, fn) { if (type === 'abort') this._listeners.push(fn); }
  removeEventListener(type, fn) { this._listeners = this._listeners.filter(l => l !== fn); }
  throwIfAborted() { if (this.aborted) throw this.reason; }
}
globalThis.AbortSignal = AbortSignalImpl;
globalThis.AbortController = class AbortController {
  constructor() { this.signal = new AbortSignalImpl(); }
  abort(reason) {
    const s = this.signal;
    if (s.aborted) return;
    s.aborted = true;
    s.reason = reason ?? new OctopusError('Cancelled', 'The operation was cancelled.');
    for (const fn of s._listeners) { try { fn({ type: 'abort' }); } catch (e) { reportError(e); } }
  }
};

globalThis.TextEncoder = class TextEncoder {
  get encoding() { return 'utf-8'; }
  encode(text = '') {
    const out = [];
    for (const ch of String(text)) {
      let c = ch.codePointAt(0);
      if (c < 0x80) out.push(c);
      else if (c < 0x800) out.push(0xc0 | (c >> 6), 0x80 | (c & 63));
      else if (c < 0x10000) out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
      else out.push(0xf0 | (c >> 18), 0x80 | ((c >> 12) & 63), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
    }
    return new Uint8Array(out);
  }
};
globalThis.TextDecoder = class TextDecoder {
  get encoding() { return 'utf-8'; }
  decode(bytes) {
    if (!bytes) return '';
    const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes.buffer ?? bytes);
    let out = '';
    for (let i = 0; i < b.length;) {
      const x = b[i++];
      let c;
      if (x < 0x80) c = x;
      else if (x < 0xe0) c = ((x & 31) << 6) | (b[i++] & 63);
      else if (x < 0xf0) c = ((x & 15) << 12) | ((b[i++] & 63) << 6) | (b[i++] & 63);
      else c = ((x & 7) << 18) | ((b[i++] & 63) << 12) | ((b[i++] & 63) << 6) | (b[i++] & 63);
      out += String.fromCodePoint(c);
    }
    return out;
  }
};
globalThis.atob = s => String.fromCharCode(...Uint8Array.fromBase64(s));
globalThis.btoa = s => Uint8Array.from(String(s), c => {
  const code = c.charCodeAt(0);
  if (code > 255) throw new RangeError('btoa: characters outside Latin-1');
  return code;
}).toBase64();
globalThis.structuredClone = value => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));

// ───────────────────────── env, l10n, permissions ─────────────────────────

const envSnapshot = sync('env') ?? {};
export const env = Object.freeze({
  appVersion: envSnapshot.appVersion,
  apiVersion: envSnapshot.apiVersion,
  platform: envSnapshot.platform,
  get language() { return sync('env')?.language ?? envSnapshot.language; },
  isDev: !!envSnapshot.isDev,
  onDidChangeLanguage: onHost('env.language'),
});

let bundle;
export const l10n = Object.freeze({
  t(key, args) {
    bundle ??= sync('l10n.bundle') ?? {};
    let text = bundle[key] ?? key;
    if (args) for (const [k, v] of Object.entries(args)) text = text.split(`{${k}}`).join(String(v));
    return text;
  },
});
onHost('env.language')(() => { bundle = undefined; });

export const permissions = Object.freeze({
  has: permission => !!sync('permissions.has', { permission }),
  request: (permission, reason) => request('permissions.request', { permission, reason }),
  onDidChange: onHost('permissions.changed'),
});

// ───────────────────────── commands ─────────────────────────

export const commands = Object.freeze({
  register(id, handler) {
    const h = handle(`command:${id}`, (...args) => handler(...args));
    notify('commands.register', { id });
    return disposable(() => { h.dispose(); notify('commands.unregister', { id }); });
  },
  execute: (id, ...args) => request('commands.execute', { id, args }),
  list: () => request('commands.list'),
});

// ───────────────────────── projects, workspace ─────────────────────────

export const projects = Object.freeze({
  get current() { return sync('projects.current') ?? undefined; },
  onDidChangeCurrent: onHost('projects.current'),
  all: () => request('projects.all'),
});

export const workspace = Object.freeze({
  items: projectId => request('workspace.items', { projectId }),
  get activeItem() { return sync('workspace.activeItem') ?? undefined; },
  onDidChangeActiveItem: onHost('workspace.activeItem'),
  onDidOpenItem: onHost('workspace.opened'),
  onDidCloseItem: onHost('workspace.closed'),
  openFile: (path, options) => request('workspace.openFile', { path, ...options }),
  openView: (viewId, options) => request('workspace.openView', { viewId, ...options }),
  activate: itemId => request('workspace.activate', { itemId }),
  close: itemId => request('workspace.close', { itemId }),
});

// ───────────────────────── file system ─────────────────────────

const bytesOut = bytes => (bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes)).toBase64();
const bytesIn = base64 => Uint8Array.fromBase64(base64 ?? '');

export const fs = Object.freeze({
  stat: path => request('fs.stat', { path }),
  exists: path => request('fs.exists', { path }),
  list: directory => request('fs.list', { directory }),
  readText: (path, options = {}) => cancellable('fs.readText', { path, encoding: options.encoding }, options.signal),
  readBytes: (path, options = {}) => request('fs.readBytes', { path, offset: options.offset, length: options.length }).then(bytesIn),
  isBinary: path => request('fs.isBinary', { path }),
  writeText: (path, text, options = {}) => request('fs.writeText', { path, create: options.create ?? true, overwrite: options.overwrite ?? true }, String(text)),
  writeBytes: (path, bytes, options = {}) => request('fs.writeBytes', { path, create: options.create ?? true, overwrite: options.overwrite ?? true }, bytesOut(bytes)),
  appendText: (path, text) => request('fs.appendText', { path }, String(text)),
  createDirectory: (path, options = {}) => request('fs.createDirectory', { path, recursive: options.recursive ?? true }),
  rename: (from, to, options = {}) => request('fs.rename', { from, to, overwrite: !!options.overwrite }),
  copy: (from, to, options = {}) => request('fs.copy', { from, to, overwrite: !!options.overwrite, recursive: !!options.recursive }),
  delete: (path, options = {}) => request('fs.delete', { path, recursive: !!options.recursive, permanent: !!options.permanent }),
  watch: (path, options, listener) => onHost('fs.watch', { path, recursive: !!options?.recursive })(listener),
  findFiles: (root, glob, options = {}) => cancellable('fs.findFiles', { root, glob, limit: options.limit }, options.signal),
  searchNames: (root, query, options = {}) => cancellable('fs.searchNames', { root, query, limit: options.limit }, options.signal),
  async searchText(root, query, options) {
    const sub = onHost('fs.searchText', { root, query })(batch => options.onMatch(batch));
    try { await cancellable('fs.searchText', { root, query }, options.signal); } finally { sub.dispose(); }
  },
  readLines: (path, options = {}) => request('fs.readLines', { path, ...options }),
  tail: (path, options) => request('fs.tail', { path, ...options }),
  scanJsonLines: (path, options) => cancellable('fs.scanJsonLines', { path, fromOffset: options.fromOffset, where: options.where, select: options.select, limit: options.limit }, options.signal),
  statMany: target => request('fs.statMany', Array.isArray(target) ? { paths: target } : target),
  async open(path, mode) {
    const { handle: h } = await request('fs.open', { path, mode });
    return {
      read: (offset, length) => request('fs.handle.read', { handle: h, offset, length }).then(bytesIn),
      write: (offset, bytes) => request('fs.handle.write', { handle: h, offset }, bytesOut(bytes)),
      close: () => request('fs.handle.close', { handle: h }),
    };
  },
  scopes: () => sync('fs.scopes') ?? [],
  requestAccess: options => request('fs.requestAccess', options),
});

export const path = (() => {
  const windows = envSnapshot.platform === 'windows';
  const sep = windows ? '\\' : '/';
  const isSep = c => c === '/' || (windows && c === '\\');
  const isAbsolute = p => (windows ? /^([a-zA-Z]:[\\/]|\\\\)/.test(p) : p.startsWith('/'));
  function normalize(p) {
    if (!p) return '.';
    const abs = isAbsolute(p);
    const prefix = windows && /^[a-zA-Z]:/.test(p) ? p.slice(0, 2) : '';
    const body = prefix ? p.slice(2) : p;
    const out = [];
    for (const part of body.split(windows ? /[\\/]+/ : /\/+/)) {
      if (part === '' || part === '.') continue;
      if (part === '..') { if (out.length && out[out.length - 1] !== '..') out.pop(); else if (!abs) out.push('..'); continue; }
      out.push(part);
    }
    const joined = out.join(sep);
    return prefix + (abs ? sep : '') + joined || (abs ? prefix + sep : '.');
  }
  const trim = p => { let e = p.length; while (e > 1 && isSep(p[e - 1])) e--; return p.slice(0, e); };
  return Object.freeze({
    sep,
    isAbsolute,
    normalize,
    join: (...parts) => normalize(parts.filter(p => p !== '').join(sep)),
    dirname(p) { p = trim(p); let i = p.length - 1; while (i >= 0 && !isSep(p[i])) i--; return i < 0 ? '.' : i === 0 ? p[0] : p.slice(0, i); },
    basename(p, ext) { p = trim(p); let i = p.length - 1; while (i >= 0 && !isSep(p[i])) i--; const b = p.slice(i + 1); return ext && b.endsWith(ext) ? b.slice(0, -ext.length) : b; },
    extname(p) { const b = this.basename(p); const i = b.lastIndexOf('.'); return i <= 0 ? '' : b.slice(i); },
    relative(from, to) {
      const a = normalize(from).split(sep).filter(Boolean), b = normalize(to).split(sep).filter(Boolean);
      const same = (x, y) => (windows ? x.toLowerCase() === y.toLowerCase() : x === y);
      let i = 0;
      while (i < a.length && i < b.length && same(a[i], b[i])) i++;
      return [...Array(a.length - i).fill('..'), ...b.slice(i)].join(sep);
    },
  });
})();

// ───────────────────────── config and storage ─────────────────────────

export const config = Object.freeze({
  get: key => sync('config.get', { key }),
  set: (key, value) => request('config.set', { key, value }),
  /** Runs the command the user entered for a `command` setting; resolves to { exitCode, stdout, stderr, timedOut, elapsedMs }. */
  runCommand: key => request('config.runCommand', { key }),
  onDidChange: onHost('config.changed'),
});

function memento(scope, projectId) {
  return Object.freeze({
    get: key => request('storage.get', { scope, projectId, key }),
    set: (key, value) => request('storage.set', { scope, projectId, key, value }),
    delete: key => request('storage.delete', { scope, projectId, key }),
    keys: () => request('storage.keys', { scope, projectId }),
  });
}

// ───────────────────────── interface trees ─────────────────────────

/** The view whose open()/render() is running: ui.state() made during it invalidates that view. */
let activeView = null;

function child(c, out) {
  if (c === null || c === undefined || c === false || c === true) return;
  if (Array.isArray(c)) { for (const x of c) child(x, out); return; }
  if (typeof c === 'string' || typeof c === 'number') { out.push({ t: 'Text', p: { value: String(c) } }); return; }
  out.push(c);
}

function node(t, props, children) {
  const p = { ...(props ?? {}) };
  const k = p.key;
  delete p.key;
  const c = [];
  child(children, c);
  const n = { t, p };
  if (k !== undefined) n.k = String(k);
  if (c.length) n.c = c;
  return n;
}

const Fragment = Symbol('Fragment');
function h(type, props, ...children) {
  if (type === Fragment) { const c = []; child(children, c); return node('Column', { gap: 0 }, c); }
  if (typeof type === 'function') return type({ ...(props ?? {}), children });
  return node(type, props, children);
}

const leaf = t => props => node(t, props);
const container = t => (props, children) => node(t, props, children);

export const ui = Object.freeze({
  h, Fragment,
  node,
  state(initial) {
    let value = initial;
    const view = activeView;
    return { get: () => value, set(v) { value = v; view?.invalidate(); } };
  },
  // layout
  column: container('Column'), row: container('Row'), grid: container('Grid'), stack: container('Stack'),
  scroll: (props, c) => node('Scroll', props, [c]), split: container('Split'), card: container('Card'), section: container('Section'),
  tabs: (props, content) => node('Tabs', props, [content]), toolbar: container('Toolbar'),
  divider: () => node('Divider'), spacer: () => node('Spacer'),
  // display
  text: (value, props) => node('Text', { ...props, value: String(value) }),
  markdown: (source, props) => node('Markdown', { ...props, source: String(source) }),
  code: (source, props) => node('Code', { ...props, source: String(source) }),
  icon: (ref, props) => node('Icon', { ...props, icon: ref }),
  image: (src, props) => node('Image', { ...props, src }),
  badge: (text, props) => node('Badge', { ...props, text: String(text) }),
  progress: leaf('Progress'), spinner: leaf('Spinner'), skeleton: leaf('Skeleton'),
  alert: leaf('Alert'), emptyState: leaf('EmptyState'), kbd: (keys, props) => node('Kbd', { ...props, keys }),
  link: leaf('Link'), tooltip: (props, c) => node('Tooltip', props, [c]),
  meter: leaf('Meter'), ring: leaf('Ring'), countdown: leaf('Countdown'), relativeTime: leaf('RelativeTime'), stat: leaf('Stat'),
  barChart: leaf('BarChart'), sparkline: leaf('Sparkline'), jsonView: leaf('JsonView'),
  // input
  button: leaf('Button'), iconButton: leaf('IconButton'), input: leaf('Input'), textarea: leaf('Textarea'),
  password: leaf('Password'), search: leaf('Search'), numberField: leaf('NumberField'), select: leaf('Select'),
  combobox: leaf('Combobox'), checkbox: leaf('Checkbox'), switch: leaf('Switch'), radioGroup: leaf('RadioGroup'),
  segmented: leaf('Segmented'), slider: leaf('Slider'), tagInput: leaf('TagInput'), pathPicker: leaf('PathPicker'),
  form: container('Form'), field: container('Field'),
  // data
  list: leaf('List'), tree: leaf('Tree'), table: leaf('Table'),
  menu: (props, c) => node('Menu', props, [c]),
  popover: (props, trigger, content) => node('Popover', props, [trigger, content]),
  // imperative
  toast: (message, options = {}) => {
    const actions = (options.actions ?? []).map((a, i) => ({ label: a.label, command: a.command, handler: a.onClick ? oneShot(a.onClick) : undefined }));
    notify('ui.toast', { message: String(message), kind: options.kind ?? 'info', actions });
  },
  confirm: options => request('ui.confirm', options),
  async prompt(options) {
    const { validate, ...rest } = options;
    const value = await request('ui.prompt', rest);
    if (value !== undefined && validate) { const error = validate(value); if (error) { ui.toast(error, { kind: 'warning' }); return undefined; } }
    return value;
  },
  pick: (items, options) => request('ui.pick', { items, ...options }).then(id => items.find(i => i.id === id)),
  pickFile: options => request('ui.pickFile', options),
  pickFolder: options => request('ui.pickFolder', options),
  pickSaveLocation: options => request('ui.pickSaveLocation', options),
  async showDialog(options) {
    const view = createView({ instanceId: `dialog:${nextDialog++}` });
    let close;
    const closed = new Promise(resolve => { close = resolve; });
    view.instance = { render: () => options.render({ close: r => { close(r); notify('ui.dialog.close', { instanceId: view.instanceId }); }, invalidate: () => view.invalidate() }) };
    const result = request('ui.dialog.open', { instanceId: view.instanceId, title: options.title, size: options.size ?? 'md', tree: view.renderTree() });
    const value = await Promise.race([closed, result]);
    viewInstances.delete(view.instanceId);
    return value;
  },
  async withProgress(options, task) {
    const controller = new AbortController();
    const id = `progress:${nextDialog++}`;
    const sub = onHost('ui.progress.cancel', { id })(() => controller.abort());
    notify('ui.progress.start', { id, title: options.title, cancellable: !!options.cancellable });
    try {
      return await task({ report: (value, message) => notify('ui.progress.report', { id, value, message }), signal: controller.signal });
    } finally {
      sub.dispose();
      notify('ui.progress.end', { id });
    }
  },
});
let nextDialog = 1;

let nextOneShot = 1;
function oneShot(fn) {
  const key = `once:${nextOneShot++}`;
  handle(key, (...args) => { handlers.delete(key); return fn(...args); });
  return key;
}

// ───────────────────────── views, editors, panels ─────────────────────────

const viewInstances = new Map();

/**
 * Turns a tree into wire data: every function in props becomes {"$h": "<id>"}. Ids are stable per node path and
 * property, so a host holding an old id still reaches the current handler after a re-render.
 */
function serialize(tree, table) {
  const walk = (n, path) => {
    if (!n || typeof n !== 'object') return n;
    const out = { t: n.t };
    if (n.k !== undefined) out.k = n.k;
    if (n.p) {
      const p = {};
      for (const [name, value] of Object.entries(n.p)) p[name] = plain(value, `${path}:${name}`);
      out.p = p;
    }
    if (n.c) out.c = n.c.map((c, i) => walk(c, `${path}/${c && c.k !== undefined ? 'k' + c.k : i}`));
    return out;
  };
  const plain = (value, at) => {
    if (typeof value === 'function') { table.set(at, value); return { $h: at }; }
    if (Array.isArray(value)) return value.map((v, i) => plain(v, `${at}[${i}]`));
    if (value && typeof value === 'object' && !(value instanceof Uint8Array)) {
      if (value.t && (value.p || value.c)) return walk(value, at);
      const o = {};
      for (const [k, v] of Object.entries(value)) o[k] = plain(v, `${at}.${k}`);
      return o;
    }
    return value;
  };
  return walk(tree, 'r');
}

function createView(context) {
  const view = {
    instanceId: context.instanceId,
    instance: null,
    handlers: new Map(),
    visible: context.visible ?? true,
    dirty: false,
    scheduled: false,
    renderTree() {
      const table = new Map();
      activeView = view;
      try {
        const tree = view.instance && view.instance.render ? view.instance.render() : node('EmptyState', { title: '' });
        view.handlers = table;
        return serialize(tree, table);
      } finally {
        activeView = null;
      }
    },
    invalidate() {
      view.dirty = true;
      if (view.scheduled || !view.visible) return;
      view.scheduled = true;
      Promise.resolve().then(() => {
        view.scheduled = false;
        if (!view.dirty || !viewInstances.has(view.instanceId)) return;
        view.dirty = false;
        try { notify('view.update', { instanceId: view.instanceId, tree: view.renderTree() }); }
        catch (e) { reportError(e); notify('view.error', { instanceId: view.instanceId, message: String(e?.message ?? e) }); }
      });
    },
  };
  viewInstances.set(view.instanceId, view);
  return view;
}

function viewContext(view, context) {
  const sizeListeners = onHost('view.resize', { instanceId: view.instanceId });
  let size = context.size ?? { class: 'regular', width: 0, height: 0 };
  return {
    instanceId: view.instanceId,
    project: context.project,
    state: context.state,
    invalidate: () => view.invalidate(),
    saveState: () => notify('view.saveState', { instanceId: view.instanceId, state: view.instance?.getState?.() }),
    setTitle: title => notify('view.chrome', { instanceId: view.instanceId, title }),
    setIcon: icon => notify('view.chrome', { instanceId: view.instanceId, icon }),
    setBadge: badge => notify('view.chrome', { instanceId: view.instanceId, badge: badge ?? null }),
    setDirty: dirty => notify('view.chrome', { instanceId: view.instanceId, dirty: !!dirty }),
    get visible() { return view.visible; },
    onDidChangeVisibility: listener => { view.onVisibility = listener; return disposable(() => { view.onVisibility = undefined; }); },
    get size() { return size; },
    onDidResize: listener => sizeListeners(s => { size = s; listener(s); }),
    onDidFocus: onHost('view.focus', { instanceId: view.instanceId }),
  };
}

function fileDocument(info, instanceId) {
  return {
    path: info.path, name: info.name, size: info.size, isBinary: !!info.isBinary,
    readText: (options = {}) => request('document.readText', { instanceId, encoding: options.encoding }),
    readBytes: (offset, length) => request('document.readBytes', { instanceId, offset, length }).then(bytesIn),
    writeText: text => request('document.writeText', { instanceId }, String(text)),
    writeBytes: bytes => request('document.writeBytes', { instanceId }, bytesOut(bytes)),
    onDidChangeOnDisk: onHost('document.changed', { instanceId }),
    onDidRename: onHost('document.renamed', { instanceId }),
  };
}

/** Host → plugin view calls: open, render, events, visibility, close. */
function registerViewHandlers() {
  handle('view.open', async (kind, providerId, context) => {
    const provider = providers.get(`${kind}:${providerId}`);
    if (!provider) throw new OctopusError('NotFound', `No ${kind} provider "${providerId}" is registered.`);
    const view = createView(context);
    const ctx = viewContext(view, context);
    activeView = view;
    try {
      view.instance = kind === 'editor'
        ? await provider.open(fileDocument(context.document, view.instanceId), ctx)
        : await provider.open(ctx);
    } finally {
      activeView = null;
    }
    return { tree: view.renderTree(), canSave: !!view.instance?.save };
  });
  handle('view.render', instanceId => viewInstances.get(instanceId)?.renderTree() ?? null);
  handle('view.event', (instanceId, handlerId, args) => {
    const view = viewInstances.get(instanceId);
    const fn = view?.handlers.get(handlerId);
    if (!fn) return null; // the node is gone since the host rendered it
    return fn(...(args ?? []));
  });
  handle('view.visibility', (instanceId, visible) => {
    const view = viewInstances.get(instanceId);
    if (!view) return;
    view.visible = visible;
    view.onVisibility?.(visible);
    if (visible && view.dirty) view.invalidate();
  });
  handle('view.canClose', async instanceId => (await viewInstances.get(instanceId)?.instance?.canClose?.()) ?? true);
  handle('view.getState', instanceId => viewInstances.get(instanceId)?.instance?.getState?.() ?? null);
  handle('view.save', async instanceId => { await viewInstances.get(instanceId)?.instance?.save?.(); return true; });
  handle('view.close', async instanceId => {
    const view = viewInstances.get(instanceId);
    viewInstances.delete(instanceId);
    try { await view?.instance?.dispose?.(); } catch (e) { reportError(e); }
    return true;
  });
}

const providers = new Map();
function provider(kind) {
  return (id, impl) => {
    providers.set(`${kind}:${id}`, impl);
    notify('providers.register', { kind, id });
    return disposable(() => { providers.delete(`${kind}:${id}`); notify('providers.unregister', { kind, id }); });
  };
}

export const views = Object.freeze({ register: provider('view') });
export const editors = Object.freeze({ register: provider('editor') });
export const panels = Object.freeze({
  register: provider('panel'),
  setBadge: (panelId, text) => notify('panels.badge', { panelId, text: text ?? null }),
});

export const sidebar = Object.freeze({
  registerSection(sectionId, impl) {
    const reg = provider('sidebarSection')(sectionId, impl);
    handle(`sidebar.items:${sectionId}`, project => impl.getItems(project));
    if (impl.getChildren) handle(`sidebar.children:${sectionId}`, id => impl.getChildren(id));
    if (impl.onActivate) handle(`sidebar.activate:${sectionId}`, id => impl.onActivate(id));
    return Object.assign(reg, { refresh: () => notify('sidebar.refresh', { sectionId }) });
  },
});

export const decorations = Object.freeze({
  registerFileDecorationProvider(impl) {
    const key = `decorations.files:${nextOneShot++}`;
    handle(key, directory => impl.provide(directory));
    notify('decorations.files.register', { key });
    return {
      dispose: () => { handlers.delete(key); notify('decorations.files.unregister', { key }); },
      refresh: directory => notify('decorations.files.refresh', { key, directory: directory ?? null }),
    };
  },
  setItemDecoration: (itemId, decoration) => notify('decorations.item', { itemId, decoration: decoration ?? null }),
  setProjectBadge: (projectId, count) => notify('decorations.project', { projectId, count }),
});

// ───────────────────────── indicators ─────────────────────────

export const indicators = Object.freeze({
  create(id) {
    const item = {
      onClick: undefined,
      popover: undefined,
      update: state => notify('indicators.update', { id, state }),
      dispose: () => { handlers.delete(`indicator.click:${id}`); notify('indicators.remove', { id }); },
    };
    handle(`indicator.click:${id}`, () => {
      if (item.popover) {
        const view = createView({ instanceId: `indicator:${id}` });
        view.instance = item.popover;
        return { tree: view.renderTree(), instanceId: view.instanceId };
      }
      item.onClick?.();
      return null;
    });
    notify('indicators.create', { id });
    return item;
  },
});

// ───────────────────────── agents, usage ─────────────────────────

export const agents = Object.freeze({
  list: () => request('agents.list'),
  launch: (agentId, options) => request('agents.launch', { agentId, ...options }),
  onSignal: (agentId, channel, listener) => onHost('agents.signal', { agentId, channel })(listener),
  lastSignal: (agentId, channel, itemId) => sync('agents.lastSignal', { agentId, channel, itemId }) ?? undefined,
  runProbe: (agentId, probe, options = {}) => request('agents.runProbe', { agentId, probe, ...options }),
  accounts: agentId => sync('agents.accounts', { agentId }) ?? [],
  /** Every account of the agent on this machine, found by the host; [{ key, label }]. */
  discoverAccounts: agentId => request('agents.discoverAccounts', { agentId }),
  registerUsageProvider(agentId) {
    notify('usage.register', { agentId });
    return {
      update: snapshot => notify('usage.update', { agentId, snapshot }),
      clear: account => notify('usage.clear', { agentId, account: account ?? null }),
      dispose: () => notify('usage.unregister', { agentId }),
    };
  },
  usage: (agentId, account) => sync('usage.get', { agentId, account }) ?? undefined,
  /** The last snapshot of every account of the agent that the host remembers: [{ account, updatedAt, snapshot }]. */
  usageList: agentId => sync('usage.list', { agentId }) ?? [],
  onDidChangeUsage: onHost('usage.changed'),
  registerStatusProvider(agentId, impl) {
    const key = `status:${agentId}`;
    handle(key, signal => impl.onSignal(signal));
    notify('status.register', { agentId, key });
    return disposable(() => { handlers.delete(key); notify('status.unregister', { agentId }); });
  },
});

export const terminals = Object.freeze({
  list: () => request('terminals.list'),
  onDidOpen: onHost('terminals.opened'),
  onDidClose: onHost('terminals.closed'),
  onDidChangeTitle: onHost('terminals.title'),
  onDidChangeCwd: onHost('terminals.cwd'),
  onDidRunCommand: onHost('terminals.command'),
  onDidBell: onHost('terminals.bell'),
  getScreenText: (itemId, options = {}) => request('terminals.screenText', { itemId, ...options }),
  sendText: (itemId, text, options = {}) => request('terminals.sendText', { itemId, enter: !!options.enter }, String(text)),
  create: options => request('terminals.create', options),
});

export const git = Object.freeze({
  get root() { return sync('git.state')?.root; },
  get branch() { return sync('git.state')?.branch; },
  status: () => request('git.status'),
  log: options => request('git.log', options),
  diff: (path, options = {}) => request('git.diff', { path, staged: !!options.staged }),
  onDidChange: onHost('git.changed'),
});

export const tasks = Object.freeze({
  registerProvider(providerId, impl) {
    const key = `tasks:${providerId}`;
    handle(key, project => impl.provide(project));
    notify('tasks.register', { providerId, key });
    return Object.assign(disposable(() => { handlers.delete(key); notify('tasks.unregister', { providerId }); }),
      { refresh: () => notify('tasks.refresh', { providerId }) });
  },
});

// ───────────────────────── theme, settings, system ─────────────────────────

export const theme = Object.freeze({
  get current() { return sync('theme.current'); },
  onDidChange: onHost('theme.changed'),
  register: (id, definition) => { notify('theme.register', { id, definition }); return disposable(() => notify('theme.unregister', { id })); },
  setOverrides(partial) {
    const key = `override:${nextOneShot++}`;
    notify('theme.override', { key, definition: partial });
    return disposable(() => notify('theme.override.remove', { key }));
  },
});

export const settings = Object.freeze({
  get: key => sync('settings.get', { key }),
  onDidChange: onHost('settings.changed'),
});

export const clipboard = Object.freeze({
  readText: () => request('clipboard.readText'),
  writeText: text => request('clipboard.writeText', {}, String(text)),
});

export const notifications = Object.freeze({
  show: (title, body, options = {}) => request('notifications.show', { title, body, handler: options.onClick ? oneShot(options.onClick) : undefined }),
});

export const shell = Object.freeze({
  open: target => request('shell.open', { target }),
  revealInFileManager: p => request('shell.reveal', { path: p }),
});

export const net = Object.freeze({
  async fetch(url, options = {}) {
    const body = options.body instanceof Uint8Array ? bytesOut(options.body) : options.body;
    const r = await cancellable('net.fetch', {
      url, method: options.method ?? 'GET', headers: options.headers ?? {},
      bodyIsBase64: options.body instanceof Uint8Array, auth: options.auth,
    }, options.signal, body ?? '');
    const bytes = () => Promise.resolve(bytesIn(r.body));
    const text = () => bytes().then(b => new TextDecoder().decode(b));
    return { status: r.status, headers: r.headers, ok: r.status >= 200 && r.status < 300, bytes, text, json: () => text().then(JSON.parse) };
  },
});

export const process = Object.freeze({
  run: (command, args, options = {}) => cancellable('process.run', { command, args, cwd: options.cwd, env: options.env, timeoutMs: options.timeoutMs }, options.signal, options.stdin ?? ''),
});

export const secrets = Object.freeze({
  get: name => request('secrets.get', { name }),
  set: (name, value) => request('secrets.set', { name }, String(value)),
  delete: name => request('secrets.delete', { name }),
});

export const app = Object.freeze({
  get state() { return sync('app.state') ?? { focused: true, visible: true, idleSeconds: 0 }; },
  onDidChangeState: onHost('app.state'),
  onDidWake: onHost('app.wake'),
});

export const scheduler = Object.freeze({
  every(intervalMs, task, options = {}) {
    const interval = Math.max(1000, intervalMs | 0);
    let failures = 0;
    let missed = false;
    let running = false;
    const run = async () => {
      if (running) return;
      if (options.whenVisible && !app.state.visible) { missed = true; return; }
      running = true;
      try { await task(); failures = 0; }
      catch (e) { failures++; reportError(e); }
      finally { running = false; }
    };
    const jitter = () => (options.jitter ? Math.random() * options.jitter * interval : 0);
    let timer;
    const schedule = () => {
      const backoff = options.backoff && failures ? Math.min(2 ** failures, 32) : 1;
      timer = setTimeout(async () => { await run(); schedule(); }, interval * backoff + jitter());
    };
    schedule();
    const subs = [];
    if (options.whenVisible) subs.push(app.onDidChangeState(s => { if (s.visible && missed) { missed = false; run(); } }));
    if (options.runOnWake) subs.push(app.onDidWake(() => run()));
    return disposable(() => { clearTimeout(timer); subs.forEach(s => s.dispose()); });
  },
});

export const workers = Object.freeze({
  spawn() { throw new OctopusError('Unsupported', 'Workers are not available in this version of Octopus.'); },
});

// ───────────────────────── dispatch ─────────────────────────

const subscriptionsOf = { list: [] };

function run(id, fn, args) {
  Promise.resolve()
    .then(() => fn(...args))
    .then(
      r => native.post('result', id, '', JSON.stringify(r === undefined ? null : r), ''),
      e => native.post('error', id, '', JSON.stringify({
        code: e instanceof OctopusError ? e.code : 'PluginError',
        message: String(e && e.message !== undefined ? e.message : e),
        stack: e && e.stack ? String(e.stack) : '',
      }), ''));
}

function dispatch(kind, id, name, json, text, fn) {
  switch (kind) {
    case 'resolve': {
      const p = pending.get(id);
      pending.delete(id);
      if (p) p.resolve(json === '' ? (text !== '' ? text : undefined) : JSON.parse(json));
      return;
    }
    case 'reject': {
      const p = pending.get(id);
      pending.delete(id);
      const e = fromJson(json) ?? {};
      if (p) p.reject(new OctopusError(e.code ?? 'Internal', e.message ?? 'The host call failed.', e));
      return;
    }
    case 'event':
      emitLocal(name, json === '' ? (text !== '' ? text : undefined) : JSON.parse(json));
      return;
    case 'invoke': {
      const handler = handlers.get(name);
      if (!handler) {
        native.post('error', id, '', JSON.stringify({ code: 'NotFound', message: `Nothing handles "${name}".` }), '');
        return;
      }
      run(id, handler, fromJson(json) ?? []);
      return;
    }
    case 'timer': {
      const timer = timers.get(id);
      if (!timer) return;
      if (!timer.repeat) timers.delete(id);
      try { timer.fn(...timer.args); } catch (e) { reportError(e); }
      return;
    }
    case 'lifecycle':
      if (name === 'activate') {
        const info = JSON.parse(json);
        const ctx = {
          id: info.id, version: info.version, pluginPath: info.pluginPath, dataPath: info.dataPath, tmpPath: info.tmpPath,
          subscriptions: subscriptionsOf.list,
          globalState: memento('global'),
          projectState: projectId => memento('project', projectId),
          log: globalThis.console,
        };
        run(id, fn, [ctx]);
      } else if (name === 'deactivate') {
        run(id, async () => {
          try { if (fn) await fn(); } finally {
            for (const s of subscriptionsOf.list.splice(0).reverse()) { try { s?.dispose?.(); } catch (e) { reportError(e); } }
          }
        }, []);
      }
      return;
  }
}

registerViewHandlers();
native.register(dispatch);
