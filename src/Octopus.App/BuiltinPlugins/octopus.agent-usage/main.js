// Agent usage (design/plugins/12-内置插件-智能体用量.md): subscription windows and session cost of the coding agents
// installed here, from their official sources. The host collects (signals, probes); this plugin converts, decides
// when to ask, warns at thresholds and shows the results (an indicator with a popover, and a history tab).

import { agents, app, commands, config, env, indicators, notifications, permissions, scheduler, ui, views, workspace } from 'octopus';
import * as adapters from './adapters.js';
import { renderPopover } from './popover.js';
import { HistoryView } from './history.js';

const AGENTS = ['claude', 'codex', 'grok', 'cursor', 'opencode'];
const CODEX_INTERVAL = 5 * 60_000;
const CURSOR_INTERVAL = 24 * 60 * 60_000;
// A cached reading younger than this is used as it is: opening the app does not ask again. Slightly under the
// refresh interval, so a scheduled run (which may come a little early) still finds the reading stale.
const CODEX_FRESH = 3 * 60_000;
const CURSOR_FRESH = 20 * 60 * 60_000;

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);

/** agentId → { name, installed, snapshots: Map<account, {snapshot, updatedAt}>, error? } */
const state = new Map();
let indicator;
let invalidatePopover = () => {};
let providers = {};
let lastUsed = {};          // agentId → ms of the latest activity we heard of
const opencodeSessions = new Map();
const claudePlans = new Map(); // account key → "Max 20x"
const signedOut = new Set();  // Claude accounts whose login is gone: no row waiting for data that cannot come
const notified = new Map(); // `${agent}:${account}:${window}` → highest threshold notified, or 'reset'

export async function activate(ctx) {
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
      return renderPopover(view());
    },
  };
  ctx.subscriptions.push(indicator);

  // Claude: every status line refresh of a running session carries the windows (official, design 12 §4.1). With a
  // custom command as the source, the windows come from the command and the status line only adds the session cost.
  if (state.get('claude')?.installed && permissions.has('agents.signals.claude.statusline')) {
    ctx.subscriptions.push(agents.onSignal('claude', 'statusline', e => {
      lastUsed.claude = Date.now();
      const snapshot = adapters.claudeFromStatusLine(e.payload, e.account);
      if (!snapshot) return;
      if (claudeSource() === 'command') mergeSession('claude', snapshot.session);
      else publish('claude', e.account, snapshot);
      if (e.account && !claudePlans.has(e.account)) refreshClaudePlans();
    }));
    const last = agents.lastSignal('claude', 'statusline');
    if (last && claudeSource() !== 'command') publish('claude', last.account, adapters.claudeFromStatusLine(last.payload, last.account), last.receivedAt);
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

  // Grok: no quota source; a status line, if Grok ever lets one be attached, gives the session cost.
  if (state.get('grok')?.installed) {
    publish('grok', '', adapters.grokSnapshot());
    if (permissions.has('agents.signals.grok.statusline')) {
      ctx.subscriptions.push(agents.onSignal('grok', 'statusline', e => {
        lastUsed.grok = Date.now();
        publish('grok', e.account, adapters.grokSnapshot(e.payload));
      }));
    }
  }

  // OpenCode: the forwarder plugin reports session.usage.updated; sessions are summed.
  if (state.get('opencode')?.installed) {
    publish('opencode', '', adapters.opencodeFromSessions(opencodeSessions));
    if (permissions.has('agents.signals.opencode.session-usage')) {
      ctx.subscriptions.push(agents.onSignal('opencode', 'session-usage', e => {
        const usage = adapters.opencodeUsageEvent(e.payload);
        if (!usage) return;
        lastUsed.opencode = Date.now();
        opencodeSessions.set(usage.id, usage);
        publish('opencode', '', adapters.opencodeFromSessions(opencodeSessions));
      }));
    }
  }

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
  await Promise.all([refreshCodex(force), refreshCursor(force), refreshClaudeCommand(force), refreshClaudePlans(force)]);
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
  refreshViews();
}

// ───────────────────────── thresholds ─────────────────────────

function thresholds() {
  const list = config.get('octopus.agent-usage.notifyAt');
  return (Array.isArray(list) ? list : ['80', '95']).map(Number).filter(n => n > 0 && n <= 100).sort((a, b) => a - b);
}

function warn(agentId, account, previous, snapshot) {
  const name = state.get(agentId)?.name ?? agentId;
  for (const w of snapshot.windows ?? []) {
    const key = `${agentId}:${account}:${w.id}`;
    const crossed = thresholds().filter(n => w.usedPercent >= n).pop();
    const before = notified.get(key);
    if (crossed !== undefined && (typeof before !== 'number' || crossed > before)) {
      notified.set(key, crossed);
      // The first reading after start only sets the baseline: no note for something that was already true.
      if (previous) {
        notify(t(`${name}：${w.label}窗口已用 ${Math.round(w.usedPercent)}%`, `${name}: ${Math.round(w.usedPercent)}% of the ${w.label} window used`),
          w.resetsAt ? t(`${formatReset(w.resetsAt)}重置`, `Resets ${formatReset(w.resetsAt)}`) : undefined);
      }
    } else if (crossed === undefined && typeof before === 'number') {
      notified.delete(key);
    }
  }
}

function checkResets() {
  const now = Date.now();
  for (const [agentId, entry] of state) {
    for (const [account, { snapshot }] of entry.snapshots) {
      for (const w of snapshot.windows ?? []) {
        if (!w.resetsAt || w.resetsAt > now || w.reset) continue;
        w.reset = true;
        const key = `${agentId}:${account}:${w.id}`;
        if (typeof notified.get(key) === 'number' && config.get('octopus.agent-usage.notifyOnReset') !== false) {
          notify(t(`${entry.name}：${w.label}窗口已重置`, `${entry.name}: the ${w.label} window has reset`));
        }
        notified.delete(key);
      }
    }
  }
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

// ───────────────────────── showing ─────────────────────────

/** What the popover and the indicator show: installed agents in a fixed order, without the hidden ones. */
function view() {
  const hide = hidden();
  const rows = [];
  for (const id of AGENTS) {
    const entry = state.get(id);
    if (!entry?.installed || hide.includes(id)) continue;
    const accounts = accountsOf(id);
    // Accounts by their names ("55232/config"), not their keys, when an agent has several.
    const labels = new Map(accounts.map(a => [a.key, a.label]));
    const snapshots = [...entry.snapshots.entries()]
      .map(([account, value]) => ({ account, ...value, snapshot: withoutPassed(value.snapshot) }));
    // Claude reports only while one of its sessions runs: an account that has not yet is listed with what to do.
    if (id === 'claude' && claudeSource() !== 'command') {
      for (const { key } of accounts) {
        if (entry.snapshots.has(key) || signedOut.has(key)) continue;
        snapshots.push({
          account: key, updatedAt: undefined,
          snapshot: {
            windows: [], plan: claudePlans.get(key) ?? undefined,
            unavailable: { reason: 'noRecentSession', hint: t('还没有这个配置的用量：用它启动一个 Claude 会话，或在设置 → 插件 → 智能体用量里接入。', 'No usage from this configuration yet: start a Claude session with it, or connect it in Settings → Plugins → Agent Usage.') },
          },
        });
      }
    }
    snapshots.sort((a, b) => (labels.get(a.account) ?? a.account).localeCompare(labels.get(b.account) ?? b.account));
    rows.push({ id, name: entry.name, snapshots: snapshots.map(s => ({ ...s, label: labels.get(s.account) })), error: entry.error });
  }
  return { rows, refresh: () => refreshAll(), t, formatReset };
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

// For tests and the history view.
export const _internal = { state, view, publish };
void app;
