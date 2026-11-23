// Agent usage (design/plugins/12-内置插件-智能体用量.md): subscription windows of the coding agents installed here,
// from their official sources, and the balance and quota of the API providers the user added a key for. The host
// collects (signals, probes); this plugin converts, decides when to ask, warns at thresholds and shows the results
// (an indicator with a tabbed popover, and a history tab).

import { agents, app, commands, config, env, indicators, notifications, permissions, scheduler, secrets, ui, views, workspace } from 'octopus';
import * as adapters from './adapters.js';
import { API_TAB, renderPopover } from './popover.js';
import { PROVIDERS, providerById } from './providers.js';
import { HistoryView } from './history.js';

const AGENTS = ['claude', 'codex', 'grok', 'cursor', 'opencode'];
const CODEX_INTERVAL = 5 * 60_000;
const CURSOR_INTERVAL = 24 * 60 * 60_000;
// A cached reading younger than this is used as it is: opening the app does not ask again. Slightly under the
// refresh interval, so a scheduled run (which may come a little early) still finds the reading stale.
const CODEX_FRESH = 3 * 60_000;
const CURSOR_FRESH = 20 * 60 * 60_000;
// API providers: every 10 minutes while the window is visible; a reading younger than 8 minutes is not asked again.
const API_INTERVAL = 10 * 60_000;
const API_FRESH = 8 * 60_000;

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);

/**
 * agentId → { name, installed, snapshots: Map<account, {snapshot, updatedAt}>, error? }. API providers live here too,
 * as `api:<provider id>` with one snapshot under '' (and `busy` while a query runs): thresholds and resets cover them.
 */
const state = new Map();
let indicator;
let invalidatePopover = () => {};
let providers = {};
let lastUsed = {};          // agentId → ms of the latest activity we heard of
// The popover's tab: what the user picked, and the agent of the active tab at that moment. Switching to another
// agent's terminal makes that agent the default again.
let picked = { tab: undefined, active: undefined };
const claudePlans = new Map(); // account key → "Max 20x"
const signedOut = new Set();  // Claude accounts whose login is gone: no row waiting for data that cannot come
// `${agent}:${account}:${window}` → { level, at, resetsAt, sent }: the highest threshold handled in this window cycle, when
// the last note went out, the cycle's reset time, and whether a note was really shown (not just the start-up baseline).
// Kept in globalState, so a restart does not forget what the user was already told.
const notices = new Map();
let store;
const NOTICES_KEY = 'notices';
const API_IDS_KEY = 'apiProviders';   // providers with a key in the secret store
const API_CACHE_KEY = 'apiCache';     // provider id → { snapshot, updatedAt }: shown at start, before asking again
let apiIds = [];
const CYCLE_SLACK = 10 * 60_000; // two readings of one window may disagree on its reset time by a little

export async function activate(ctx) {
  store = ctx.globalState;
  await loadNotices();
  await loadApi();
  const list = await agents.list();
  for (const a of list) {
    if (AGENTS.includes(a.id)) state.set(a.id, { name: a.name, installed: a.installed, snapshots: new Map() });
  }

  for (const id of AGENTS) {
    if (!state.get(id)?.installed) continue;
    providers[id] = agents.registerUsageProvider(id);
    ctx.subscriptions.push(providers[id]);
  }

  // What the host remembers from the last run is shown at once; the queries below only ask for what has gone stale.
  restoreCache();

  indicator = indicators.create('octopus.agent-usage.indicator');
  indicator.popover = {
    render: () => {
      // A state made during render belongs to this popover: setting it re-renders the popover while it is open.
      const tick = ui.state(0);
      invalidatePopover = () => tick.set(tick.get() + 1);
      return renderPopover(popoverModel());
    },
  };
  ctx.subscriptions.push(indicator);

  // Claude: every status line refresh of a running session carries the windows (official, design 12 §4.1). With a
  // custom command as the source, the windows come from the command and the status line only adds the session numbers.
  if (state.get('claude')?.installed && permissions.has('agents.signals.claude.statusline')) {
    ctx.subscriptions.push(agents.onSignal('claude', 'statusline', e => {
      lastUsed.claude = Date.now();
      const snapshot = adapters.claudeFromStatusLine(e.payload, e.account);
      if (!snapshot) return;
      if (claudeSource() === 'command') mergeSession('claude', snapshot.session);
      else publish('claude', e.account, keepHighest(state.get('claude').snapshots.get(e.account ?? '')?.snapshot, snapshot));
      if (e.account && !claudePlans.has(e.account)) refreshClaudePlans();
    }));
    const last = agents.lastSignal('claude', 'statusline');
    if (last && claudeSource() !== 'command') {
      const cached = state.get('claude').snapshots.get(last.account ?? '');
      // The cache may hold a newer reading than the last signal (a session that reported through another window).
      if (!cached || !(cached.updatedAt > last.receivedAt)) {
        publish('claude', last.account, keepHighest(cached?.snapshot, adapters.claudeFromStatusLine(last.payload, last.account)), last.receivedAt);
      }
    }
  }
  if (state.get('claude')?.installed) {
    // The plan of each account, from `claude auth status --json` (Claude reads its own login; we only get the plan).
    setTimeout(() => refreshClaudePlans(), 4_000);
    ctx.subscriptions.push(scheduler.every(60 * 60_000, () => refreshClaudePlans({ force: true }), { whenVisible: true }));
    setTimeout(() => refreshClaudeCommand(), 2_000);
    scheduleClaudeCommand();
    ctx.subscriptions.push({ dispose: () => clearTimeout(claudeTimer) });
    ctx.subscriptions.push(app.onDidWake(() => refreshClaudeCommand()));
  }

  // Grok and OpenCode: no quota source of their own; the popover says where their usage can be seen.
  if (state.get('grok')?.installed) publish('grok', '', adapters.grokSnapshot());
  if (state.get('opencode')?.installed) publish('opencode', '', adapters.opencodeSnapshot());

  // Codex: the app-server probe, soon after start, then every 5 minutes while the window is visible.
  if (state.get('codex')?.installed) {
    setTimeout(() => refreshCodex(), 3_000);
    ctx.subscriptions.push(scheduler.every(CODEX_INTERVAL, () => refreshCodex(), { whenVisible: true, runOnWake: true, jitter: 0.2, backoff: true }));
  }

  // Cursor: only the plan, once a day.
  if (state.get('cursor')?.installed) {
    setTimeout(() => refreshCursor(), 5_000);
    ctx.subscriptions.push(scheduler.every(CURSOR_INTERVAL, () => refreshCursor(), { whenVisible: true }));
  }

  // API providers with a key: soon after start, then every 10 minutes while the window is visible.
  setTimeout(() => refreshProviders(), 6_000);
  ctx.subscriptions.push(scheduler.every(API_INTERVAL, () => refreshProviders(), { whenVisible: true, runOnWake: true, jitter: 0.2 }));

  // The indicator follows the agent of the active tab.
  ctx.subscriptions.push(workspace.onDidChangeActiveItem(() => updateIndicator()));
  // Countdowns pass their reset time: a minute tick drops stale windows and fires "reset" notes.
  ctx.subscriptions.push(scheduler.every(60_000, () => { checkResets(); updateIndicator(); }));

  // Settings → Plugins edits the thresholds, the hidden agents and Claude's source: show the change at once.
  ctx.subscriptions.push(config.onDidChange(key => {
    if (typeof key === 'string' && key.startsWith('octopus.agent-usage.claude.')) switchClaudeSource();
    refreshViews();
  }));
  ctx.subscriptions.push(commands.register('octopus.agent-usage.refresh', () => refreshAll()));
  ctx.subscriptions.push(views.register('octopus.agent-usage.history', { open: viewContext => new HistoryView(viewContext) }));

  updateIndicator();
}

export function deactivate() {
  providers = {};
}

// ───────────────────────── collecting ─────────────────────────

/**
 * Accounts of an agent: every one on this machine (the host looks for them, so none has to report first), or the ones
 * it already knows when the host cannot search.
 */
async function allAccounts(agentId) {
  try {
    return await agents.discoverAccounts(agentId);
  } catch {
    return accountsOf(agentId);
  }
}

/** True while the reading of an account is younger than `maxAge`: no need to ask again. */
function isFresh(agentId, account, maxAge) {
  const value = state.get(agentId)?.snapshots.get(account);
  return !!value && !value.snapshot.retry && Date.now() - value.updatedAt < maxAge;
}

/** Brings back the last reading of every account from the host's cache (`usage-cache.json`), with its own age. */
function restoreCache() {
  for (const id of ['claude', 'codex', 'cursor']) {
    const entry = state.get(id);
    if (!entry?.installed) continue;
    let cached;
    try {
      cached = agents.usageList(id);
    } catch {
      continue;
    }
    for (const { account, updatedAt, snapshot } of cached) {
      // A reading made with the other Claude source says nothing about this one.
      if (id === 'claude' && (snapshot.source === 'custom') !== (claudeSource() === 'command')) continue;
      // The host blanks windows past their reset (no percentage, reset: true); here they read as 0%, as a live one would.
      const restored = { ...snapshot, windows: (snapshot.windows ?? []).map(w => ({ ...w, usedPercent: w.usedPercent ?? 0 })) };
      entry.snapshots.set(account, { snapshot: restored, updatedAt });
      if (id === 'claude' && restored.plan) claudePlans.set(account, restored.plan);
      warn(id, account, undefined, restored); // the baseline: no note for what was already true
    }
  }
  refreshViews();
}

async function refreshCodex({ force = false } = {}) {
  if (!permissions.has('agents.probes.codex.rateLimits')) return;
  const accounts = await allAccounts('codex');
  const keys = accounts.length > 0 ? accounts.map(a => a.key) : [undefined];
  for (const key of keys) {
    if (!force && isFresh('codex', key ?? '', CODEX_FRESH)) continue;
    const result = await agents.runProbe('codex', 'rateLimits', key ? { account: key } : {});
    const entry = state.get('codex');
    if (result.ok) {
      entry.error = undefined;
      publish('codex', result.account ?? key ?? '', adapters.codexFromRateLimits(result.data, result.account), result.completedAt);
    } else {
      entry.error = result.message ?? result.error;
      refreshViews();
    }
  }
}

async function refreshCursor({ force = false } = {}) {
  if (!permissions.has('agents.probes.cursor.account')) return;
  if (!force && isFresh('cursor', '', CURSOR_FRESH)) return;
  const result = await agents.runProbe('cursor', 'account');
  if (result.ok) publish('cursor', '', adapters.cursorFromAbout(result.data), result.completedAt);
  else {
    state.get('cursor').error = result.message ?? result.error;
    // `retry`: this is not an answer, so the next start asks again instead of trusting it for a day.
    publish('cursor', '', { ...adapters.cursorFromAbout(undefined), plan: undefined, retry: true });
  }
}

// ───────────────────────── Claude: status line or the user's command ─────────────────────────

const CLAUDE_SOURCE = 'octopus.agent-usage.claude.source';
const CLAUDE_COMMAND = 'octopus.agent-usage.claude.command';
const CLAUDE_INTERVAL = 'octopus.agent-usage.claude.interval';
let claudeTimer;
let claudeSourceShown;

function claudeSource() {
  return config.get(CLAUDE_SOURCE) === 'command' ? 'command' : 'statusline';
}

function claudeIntervalMs() {
  const minutes = Number(config.get(CLAUDE_INTERVAL));
  return Math.max(1, Number.isFinite(minutes) ? minutes : 5) * 60_000;
}

/** Runs the command every few minutes while the window is visible; re-armed after each run, so the interval can change. */
function scheduleClaudeCommand() {
  clearTimeout(claudeTimer);
  claudeTimer = setTimeout(async () => {
    // The timer is the interval: a tick runs the command whatever the cache says.
    if (app.state.visible !== false) await refreshClaudeCommand({ force: true });
    scheduleClaudeCommand();
  }, claudeIntervalMs());
}

/** The source or the command changed: drop what the other source showed and ask again. */
function switchClaudeSource() {
  const entry = state.get('claude');
  if (!entry) return;
  const source = claudeSource();
  if (source !== claudeSourceShown) {
    entry.snapshots.clear();
    entry.error = undefined;
    providers.claude?.clear();
    claudeSourceShown = source;
  }
  scheduleClaudeCommand();
  refreshClaudeCommand({ force: true });
}

async function refreshClaudeCommand({ force = false } = {}) {
  const entry = state.get('claude');
  if (!entry?.installed || claudeSource() !== 'command') return;
  claudeSourceShown = 'command';
  if (!force && isFresh('claude', '', claudeIntervalMs() * 0.8)) return;
  if (config.get(CLAUDE_COMMAND) !== true) {
    entry.error = undefined;
    publish('claude', '', {
      windows: [], source: 'custom', retry: true,
      unavailable: { reason: 'notConfigured', hint: t('还没有填写查询命令：设置 → 插件 → 智能体用量。', 'No command yet: Settings → Plugins → Agent Usage.') },
    });
    return;
  }
  let result;
  try {
    result = await config.runCommand(CLAUDE_COMMAND);
  } catch (e) {
    if (e.code === 'Busy') return; // ran moments ago (a settings change right after a scheduled run)
    entry.error = e.message;
    refreshViews();
    return;
  }
  if (result.timedOut || result.exitCode !== 0) {
    entry.error = result.timedOut ? t('命令超时', 'The command timed out') : `${t('命令退出码', 'Exit code')} ${result.exitCode}${result.stderr ? ': ' + result.stderr.trim().split('\n').pop() : ''}`;
    refreshViews();
    return;
  }
  let data;
  try {
    data = JSON.parse(result.stdout);
  } catch {
    entry.error = t('命令的输出不是 JSON', 'The command did not print JSON');
    refreshViews();
    return;
  }
  const snapshot = adapters.fromCustom(data);
  if (!snapshot) {
    entry.error = t('命令的输出无法识别', 'The command output is not recognized');
    refreshViews();
    return;
  }
  entry.error = undefined;
  // Keep the session cost the status line reported, if any.
  const previous = entry.snapshots.get('')?.snapshot;
  publish('claude', '', { ...snapshot, session: snapshot.session ?? previous?.session });
}

/**
 * Every running Claude session reports the account's windows, each from its own last reply: an idle session repeats
 * an older, lower reading after a busy one reported a higher one, and the percentage bounced (80%, 79%, 80%…). Within
 * one cycle of a window (the same reset time) the usage only grows, so a lower reading of the same cycle is stale and
 * the higher one stays. A new reset time is a new cycle and is taken as it is.
 */
export function keepHighest(previous, snapshot) {
  if (!previous || !snapshot) return snapshot;
  const now = Date.now();
  const windows = (snapshot.windows ?? []).map(w => {
    const old = (previous.windows ?? []).find(p => p.id === w.id);
    if (!old || old.reset || !(old.usedPercent > w.usedPercent)) return w;
    if (!old.resetsAt || old.resetsAt <= now || !w.resetsAt) return w;
    return Math.abs(w.resetsAt - old.resetsAt) <= CYCLE_SLACK ? { ...w, usedPercent: old.usedPercent } : w;
  });
  return { ...snapshot, windows };
}

/** Status line session numbers added to the command's snapshot. */
function mergeSession(agentId, session) {
  const entry = state.get(agentId);
  const current = entry?.snapshots.get('');
  if (!current || !session) return;
  current.snapshot = { ...current.snapshot, session };
  providers[agentId]?.update({ ...current.snapshot });
  refreshViews();
}

/** The refresh button and the command: ask everything again, whatever the cache says. */
async function refreshAll() {
  const force = { force: true };
  await Promise.all([refreshCodex(force), refreshCursor(force), refreshClaudeCommand(force), refreshClaudePlans(force), refreshProviders(force)]);
  refreshViews();
}

/** The agent's known accounts; none when its permissions were switched off (the call is refused then). */
function accountsOf(agentId) {
  try {
    return agents.accounts(agentId);
  } catch {
    return [];
  }
}

function hidden() {
  const list = config.get('octopus.agent-usage.hidden');
  return Array.isArray(list) ? list : [];
}

async function refreshClaudePlans({ force = false } = {}) {
  if (!state.get('claude')?.installed || !permissions.has('agents.probes.claude.account')) return;
  const accounts = (await allAccounts('claude')).map(a => a.key);
  for (const key of accounts.length > 0 ? accounts : [undefined]) {
    // The plan of an account rarely changes: a known one is not asked for again at start, only by the hourly run.
    if (!force && claudePlans.has(key ?? '')) continue;
    let result;
    try {
      result = await agents.runProbe('claude', 'account', key ? { account: key } : {});
    } catch {
      continue;
    }
    if (!result.ok) continue;
    if (key && result.data?.loggedIn === false) signedOut.add(key);
    else if (key) signedOut.delete(key);
    const plan = adapters.claudePlan(result.data);
    const entry = state.get('claude');
    // A known account gets its own plan; without any (one default configuration) the plan is everyone's.
    const targets = key ? [key] : ['', ...entry.snapshots.keys()];
    for (const target of targets) {
      claudePlans.set(target, plan ?? null);
      const value = entry.snapshots.get(target);
      if (plan && value && value.snapshot.plan !== plan) {
        value.snapshot = { ...value.snapshot, plan };
        providers.claude?.update({ ...value.snapshot, account: target || undefined });
      }
    }
  }
  refreshViews();
}

function publish(agentId, account, snapshot, at) {
  if (!snapshot) return;
  const entry = state.get(agentId);
  if (!entry) return;
  const key = account ?? '';
  if (agentId === 'claude' && !snapshot.plan) {
    const plan = claudePlans.get(key) ?? (accountsOf('claude').length <= 1 ? claudePlans.get('') : undefined);
    if (plan) snapshot = { ...snapshot, plan };
  }
  const previous = entry.snapshots.get(key)?.snapshot;
  entry.snapshots.set(key, { snapshot, updatedAt: at ?? Date.now() });
  providers[agentId]?.update({ ...snapshot, account: key || undefined });
  warn(agentId, key, previous, snapshot);
  if (entry.api) saveApiCache();
  refreshViews();
}

// ───────────────────────── thresholds ─────────────────────────

function thresholds() {
  const list = config.get('octopus.agent-usage.notifyAt');
  return (Array.isArray(list) ? list : ['80', '95']).map(Number).filter(n => n > 0 && n <= 100).sort((a, b) => a - b);
}

/** The least time between two notes about one window (minutes in the setting; 0 turns the pause off). */
function noticeGapMs() {
  const minutes = Number(config.get('octopus.agent-usage.notifyInterval'));
  return Math.max(0, Number.isFinite(minutes) ? minutes : 30) * 60_000;
}

async function loadNotices() {
  let saved;
  try {
    saved = await store?.get(NOTICES_KEY);
  } catch {
    return;
  }
  const now = Date.now();
  for (const [key, rec] of Object.entries(saved ?? {})) {
    // A cycle that ended while the app was closed is over: its reset note would only be stale news.
    if (rec && typeof rec.level === 'number' && (!rec.resetsAt || rec.resetsAt > now)) notices.set(key, rec);
  }
}

function saveNotices() {
  try {
    Promise.resolve(store?.set(NOTICES_KEY, Object.fromEntries(notices))).catch(() => {});
  } catch { /* the reminders still work for this run */ }
}

/**
 * Notes when a window passes a threshold. A threshold is announced once per window cycle (until the window resets), a
 * dip and a climb back across it say nothing new, and notes about one window keep a pause between them.
 */
function warn(agentId, account, previous, snapshot) {
  const name = state.get(agentId)?.name ?? agentId;
  const levels = thresholds();
  const now = Date.now();
  let changed = false;
  for (const w of snapshot.windows ?? []) {
    // A reading from before the window's reset describes a window that no longer exists.
    if (w.reset || (w.resetsAt && w.resetsAt <= now)) continue;
    const key = `${agentId}:${account}:${w.id}`;
    let rec = notices.get(key);
    if (rec) {
      // A later reset time is a new cycle. Without a reset time, a clear drop below every threshold is.
      const renewed = w.resetsAt && rec.resetsAt ? w.resetsAt - rec.resetsAt > CYCLE_SLACK : !w.resetsAt && levels.length > 0 && w.usedPercent < levels[0] - 10;
      if (renewed) { notices.delete(key); rec = undefined; changed = true; }
    }
    const crossed = levels.filter(n => w.usedPercent >= n).pop();
    if (crossed === undefined || (rec && rec.level >= crossed)) continue;
    // The first reading after start only sets the baseline: no note for something that was already true.
    if (!previous) {
      notices.set(key, { level: crossed, at: rec?.at ?? 0, resetsAt: w.resetsAt, sent: rec?.sent ?? false });
      changed = true;
      continue;
    }
    // Too soon after the last note on this window: not recorded, so the next reading after the pause tells it.
    if (rec?.at && now - rec.at < noticeGapMs()) continue;
    notices.set(key, { level: crossed, at: now, resetsAt: w.resetsAt, sent: true });
    changed = true;
    notify(t(`${name}：${w.label}窗口已用 ${Math.round(w.usedPercent)}%`, `${name}: ${Math.round(w.usedPercent)}% of the ${w.label} window used`),
      w.resetsAt ? t(`${formatReset(w.resetsAt)}重置`, `Resets ${formatReset(w.resetsAt)}`) : undefined);
  }
  if (changed) saveNotices();
}

function checkResets() {
  const now = Date.now();
  let changed = false;
  for (const [agentId, entry] of state) {
    for (const [account, { snapshot }] of entry.snapshots) {
      for (const w of snapshot.windows ?? []) {
        if (!w.resetsAt || w.resetsAt > now || w.reset) continue;
        w.reset = true;
        const key = `${agentId}:${account}:${w.id}`;
        const rec = notices.get(key);
        if (!rec) continue;
        // Only a window the user was really warned about is worth a "reset" note.
        if (rec.sent && config.get('octopus.agent-usage.notifyOnReset') !== false) {
          notify(t(`${entry.name}：${w.label}窗口已重置`, `${entry.name}: the ${w.label} window has reset`));
        }
        notices.delete(key);
        changed = true;
      }
    }
  }
  if (changed) saveNotices();
}

async function notify(title, body) {
  if (permissions.has('notifications')) {
    try { await notifications.show(title, body); return; } catch { /* falls back to a toast */ }
  }
  ui.toast(body ? `${title} · ${body}` : title, { kind: 'warning' });
}

export function formatReset(ms) {
  const minutes = Math.max(0, Math.round((ms - Date.now()) / 60_000));
  if (minutes < 60) return t(`${minutes} 分钟后`, `in ${minutes} min`);
  if (minutes < 24 * 60) return t(`${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分后`, `in ${Math.floor(minutes / 60)} h ${minutes % 60} min`);
  const date = new Date(ms);
  return date.toLocaleString(env.language, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
}

// ───────────────────────── API providers ─────────────────────────

const secretName = id => `apiKey.${id}`;

function apiAvailable() {
  return permissions.has('net') && permissions.has('secrets');
}

function apiEntry(id) {
  const key = `api:${id}`;
  let entry = state.get(key);
  if (!entry) {
    entry = { name: providerById(id)?.name ?? id, installed: true, api: true, snapshots: new Map() };
    state.set(key, entry);
  }
  return entry;
}

async function loadApi() {
  let ids;
  let cache;
  try {
    ids = await store?.get(API_IDS_KEY);
    cache = await store?.get(API_CACHE_KEY);
  } catch { /* nothing remembered */ }
  apiIds = (Array.isArray(ids) ? ids : []).filter(id => providerById(id));
  for (const id of apiIds) {
    const entry = apiEntry(id);
    const cached = cache?.[id];
    if (!cached?.snapshot) continue;
    entry.snapshots.set('', { snapshot: cached.snapshot, updatedAt: cached.updatedAt });
    warn(`api:${id}`, '', undefined, cached.snapshot); // the baseline
  }
}

function saveApiIds() {
  try { Promise.resolve(store?.set(API_IDS_KEY, apiIds)).catch(() => {}); } catch { /* kept for this run */ }
}

function saveApiCache() {
  const cache = {};
  for (const id of apiIds) {
    const value = state.get(`api:${id}`)?.snapshots.get('');
    if (value) cache[id] = value;
  }
  try { Promise.resolve(store?.set(API_CACHE_KEY, cache)).catch(() => {}); } catch { /* kept for this run */ }
}

async function refreshProviders({ force = false } = {}) {
  if (!apiAvailable()) return;
  await Promise.all(apiIds.filter(id => force || !isFresh(`api:${id}`, '', API_FRESH)).map(id => queryProvider(id)));
}

/** Asks one provider; with `key` it checks a key that is not stored yet. Resolves to 'ok', 'refused' or 'failed'. */
async function queryProvider(id, key) {
  const provider = providerById(id);
  const entry = apiEntry(id);
  if (!provider || entry.busy) return 'failed';
  entry.busy = true;
  refreshViews();
  try {
    const secret = key ?? await secrets.get(secretName(id));
    if (!secret) {
      entry.error = t('没有找到密钥，请重新添加。', 'The key is missing; add it again.');
      return 'failed';
    }
    const snapshot = await provider.query(secret);
    entry.error = undefined;
    publish(`api:${id}`, '', { ...snapshot, source: provider.unofficial ? 'unofficial' : 'official' });
    return 'ok';
  } catch (e) {
    entry.error = e?.message ?? String(e);
    return e?.kind === 'auth' ? 'refused' : 'failed';
  } finally {
    entry.busy = false;
    refreshViews();
  }
}

/** Adds (or, given an id, replaces) a provider's key: asked in a password field, checked, then stored. */
async function addProvider(id) {
  if (!apiAvailable()) return;
  if (!id) {
    const items = PROVIDERS.filter(p => !apiIds.includes(p.id)).map(p => ({ id: p.id, label: p.name, description: p.hint }));
    const choice = await ui.pick(items, { placeholder: t('选择服务商', 'Choose a provider') });
    if (!choice) return;
    id = choice.id;
  }
  const provider = providerById(id);
  const typed = await ui.prompt({
    title: t(`${provider.name} 的 API 密钥`, `${provider.name} API key`),
    description: [
      provider.hint,
      provider.unofficial ? t('这家的用量接口没有公开文档，可能随时失效。', 'This provider’s usage endpoint is undocumented and may stop working at any time.') : '',
      t('密钥存进系统密钥库，只发往这家服务商的接口。', 'The key goes into the system keychain and is sent only to this provider’s API.'),
    ].filter(Boolean).join('\n'),
    label: t('API 密钥', 'API key'),
    placeholder: 'sk-…',
    password: true,
    confirmLabel: t('保存', 'Save'),
  });
  const key = typeof typed === 'string' ? typed.trim() : '';
  if (!key) return;
  const known = apiIds.includes(id);
  // A refused key is not kept; one that could not be checked (offline, a busy server) is, with the error shown.
  const result = await queryProvider(id, key);
  if (result === 'refused') {
    ui.toast(t(`${provider.name} 拒绝了这个密钥：${state.get(`api:${id}`)?.error ?? ''}`, `${provider.name} refused the key: ${state.get(`api:${id}`)?.error ?? ''}`), { kind: 'warning' });
    if (!known) state.delete(`api:${id}`);
    else state.get(`api:${id}`).error = undefined;
    refreshViews();
    return;
  }
  try {
    await secrets.set(secretName(id), key);
  } catch (e) {
    ui.toast(t(`密钥没有保存：${e?.message ?? e}`, `The key was not saved: ${e?.message ?? e}`), { kind: 'warning' });
    if (!known) state.delete(`api:${id}`);
    refreshViews();
    return;
  }
  if (!known) {
    apiIds = [...apiIds, id];
    saveApiIds();
  }
  saveApiCache();
  picked = { tab: API_TAB, active: workspace.activeItem?.agentId };
  refreshViews();
}

async function removeProvider(id) {
  const provider = providerById(id);
  const ok = await ui.confirm({
    title: t(`移除 ${provider?.name ?? id}？`, `Remove ${provider?.name ?? id}?`),
    description: t('它的密钥会从系统密钥库删除。', 'Its key is deleted from the system keychain.'),
    confirmLabel: t('移除', 'Remove'),
    danger: true,
  });
  if (!ok) return;
  try { await secrets.delete(secretName(id)); } catch { /* gone already */ }
  apiIds = apiIds.filter(x => x !== id);
  state.delete(`api:${id}`);
  for (const key of [...notices.keys()]) if (key.startsWith(`api:${id}:`)) notices.delete(key);
  saveNotices();
  saveApiIds();
  saveApiCache();
  refreshViews();
}

// ───────────────────────── showing ─────────────────────────

/** The installed agents in a fixed order, without the hidden ones: their accounts with data, and those still waiting. */
function agentRows() {
  const hide = hidden();
  const rows = [];
  for (const id of AGENTS) {
    const entry = state.get(id);
    if (!entry?.installed || hide.includes(id)) continue;
    const accounts = accountsOf(id);
    // Accounts by their names ("55232/config"), not their keys, when an agent has several.
    const labels = new Map(accounts.map(a => [a.key, a.label]));
    const byName = (a, b) => (labels.get(a.account) ?? a.account).localeCompare(labels.get(b.account) ?? b.account);
    const snapshots = [...entry.snapshots.entries()]
      .map(([account, value]) => ({ account, ...value, snapshot: withoutPassed(value.snapshot), label: labels.get(account) }))
      .sort(byName);
    // Claude reports only while one of its sessions runs: accounts that have not yet are named, with what to do.
    const waiting = id === 'claude' && claudeSource() !== 'command'
      ? accounts.filter(a => !entry.snapshots.has(a.key) && !signedOut.has(a.key)).map(a => ({ account: a.key, label: a.label })).sort(byName)
      : [];
    rows.push({ id, name: entry.name, snapshots, waiting, error: entry.error });
  }
  return rows;
}

function highest(snapshots) {
  let best;
  for (const { snapshot } of snapshots) {
    const w = adapters.tightest(snapshot);
    if (w && (best === undefined || w.usedPercent > best)) best = w.usedPercent;
  }
  return best;
}

function apiModel() {
  return {
    available: apiAvailable(),
    providers: apiIds.map(id => {
      const provider = providerById(id);
      const entry = state.get(`api:${id}`);
      const value = entry?.snapshots.get('');
      return {
        id, name: provider.name, unofficial: !!provider.unofficial, console: provider.console,
        snapshot: value && withoutPassed(value.snapshot), updatedAt: value?.updatedAt, error: entry?.error, busy: !!entry?.busy,
      };
    }),
    addable: PROVIDERS.filter(p => !apiIds.includes(p.id)),
    add: () => addProvider(),
    replace: id => addProvider(id),
    remove: id => removeProvider(id),
  };
}

/** What the popover shows: a tab per agent (with its tightest window) and one for the API providers. */
function popoverModel() {
  const rows = agentRows();
  const api = apiModel();
  // "Claude Code", "Codex CLI": the product word is dropped in the tab, where width is short.
  const tabs = rows.map(r => ({ id: r.id, name: r.name.replace(/\s+(Code|CLI)$/i, ''), percent: highest(r.snapshots) }));
  if (api.available && !hidden().includes('api')) {
    tabs.push({ id: API_TAB, name: t('API 余额', 'API balance'), percent: highest(api.providers.filter(p => p.snapshot)) });
  }
  const active = workspace.activeItem?.agentId;
  const keep = tabs.some(tab => tab.id === picked.tab) && (picked.active === active || !tabs.some(tab => tab.id === active));
  const selected = keep ? picked.tab
    : tabs.find(tab => tab.id === active)?.id ?? tabs.find(tab => tab.percent !== undefined)?.id ?? tabs[0]?.id;
  return {
    // Compact unless the user chose comfortable (Settings → Plugins → Agent Usage).
    style: config.get('octopus.agent-usage.style') === 'comfortable' ? 'comfortable' : 'compact',
    tabs, selected, api, t,
    agent: id => rows.find(r => r.id === id),
    select: id => {
      if (id === picked.tab && picked.active === active) return;
      picked = { tab: id, active };
      invalidatePopover();
    },
    refresh: () => refreshAll(),
  };
}

/** Windows whose reset time has passed are shown as reset (0%), not as the stale value. */
function withoutPassed(snapshot) {
  const now = Date.now();
  return {
    ...snapshot,
    windows: (snapshot.windows ?? []).map(w => (w.resetsAt && w.resetsAt <= now ? { ...w, usedPercent: 0, resetsAt: undefined, stale: true } : w)),
  };
}

function updateIndicator() {
  if (!indicator) return;
  const active = workspace.activeItem;
  const hide = hidden();
  // The active tab's agent first; otherwise the most used window of any agent.
  const order = active?.agentId && AGENTS.includes(active.agentId) ? [active.agentId, ...AGENTS.filter(a => a !== active.agentId)] : AGENTS;
  let shown;
  for (const id of order) {
    const entry = state.get(id);
    if (!entry?.installed || hide.includes(id)) continue;
    for (const { snapshot } of entry.snapshots.values()) {
      const w = adapters.tightest(withoutPassed(snapshot));
      if (w && (!shown || (id === order[0] && shown.id !== id) || (shown.id !== order[0] && w.usedPercent > shown.window.usedPercent))) {
        shown = { id, name: entry.name, window: w };
      }
    }
    if (shown?.id === order[0] && active?.agentId === id) break;
  }
  if (shown) {
    const p = Math.round(shown.window.usedPercent);
    indicator.update({
      text: `${p}%`,
      value: Math.min(1, shown.window.usedPercent / 100),
      level: p >= 90 ? 'danger' : p >= 75 ? 'warning' : 'default',
      tooltip: `${shown.name} · ${shown.window.label} ${p}%${shown.window.resetsAt ? ' · ' + t(`${formatReset(shown.window.resetsAt)}重置`, `resets ${formatReset(shown.window.resetsAt)}`) : ''}`,
      icon: null,
      visible: true,
    });
    return;
  }
  // No windows anywhere: show the gauge icon so the popover stays reachable.
  const any = [...state.values()].some(e => e.installed);
  indicator.update({ text: null, value: null, level: null, icon: 'lucide:gauge', tooltip: t('智能体用量', 'Agent usage'), visible: any });
}

let refreshQueued = false;
function refreshViews() {
  if (refreshQueued) return;
  refreshQueued = true;
  queueMicrotask(() => {
    refreshQueued = false;
    updateIndicator();
    invalidatePopover();
  });
}

// For tests.
export const _internal = { state, popoverModel, publish };
void app;
