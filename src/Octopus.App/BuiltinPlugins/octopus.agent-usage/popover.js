// The indicator's popover (design/plugins/12 §8): one tab per installed agent and one for the API providers. Only
// usage: plan, windows with reset countdowns and pace, balance, freshness; where a quota cannot be read, the reason
// and what to do instead. Costs live in the history tab, not here. The host scrolls the popover when it is taller
// than its cap.
//
// Two styles (setting octopus.agent-usage.style): compact (the default) packs a window into one short line with short
// labels ("5h", "4h07") and leaves the details to the row's tooltip; comfortable spells things out with more room.

import { commands, env, shell, ui } from 'octopus';
import { money } from './providers.js';

const API_TAB = 'api';

/** Sizes of each style. Spacing is in the host's 4 px steps; widths in pixels. */
const STYLES = {
  compact: {
    compact: true, width: 400, gap: 1, sectionGap: 2, rowGap: 0.75, cellGap: 1.5,
    labelWidth: 52, percentWidth: 38, resetWidth: 68, barHeight: 4, divider: false,
  },
  comfortable: {
    compact: false, width: 440, gap: 2, sectionGap: 3, rowGap: 1.5, cellGap: 2,
    labelWidth: 76, percentWidth: 42, resetWidth: 92, barHeight: undefined, divider: true,
  },
};

export const styleOf = name => STYLES[name] ?? STYLES.compact;

/**
 * model: { style, tabs: [{ id, name, percent? }], selected, select(id), refresh(), agent(id) → row, api, t }
 */
export function renderPopover(model) {
  const { tabs, t } = model;
  const s = styleOf(model.style);
  const note = model.selected === API_TAB
    ? t('密钥存在系统密钥库，只发往对应服务商的接口。', 'Keys stay in the system keychain and go only to their provider’s API.')
    : t('额度来自各智能体的官方接口，只在本机处理。', 'Quotas from each agent’s official interface, processed on this machine only.');
  const header = ui.row({ gap: s.gap, align: 'center' }, [
    // Compact: the note moves into the title's tooltip instead of taking a line of its own.
    ui.text(t('智能体用量', 'Agent usage'), { variant: 'heading', grow: 1, tooltip: s.compact ? note : undefined }),
    ui.iconButton({ icon: 'lucide:chart-column', tooltip: t('用量历史', 'Usage history'), command: 'octopus.agent-usage.history.open' }),
    ui.iconButton({ icon: 'lucide:refresh-cw', tooltip: t('刷新', 'Refresh'), onClick: model.refresh }),
  ]);
  if (tabs.length === 0) {
    return ui.column({ gap: s.gap, width: s.width }, [
      header,
      ui.emptyState({
        icon: 'lucide:gauge',
        title: t('没有发现智能体', 'No agents found'),
        description: t('安装 Claude、Codex、Antigravity、Copilot、Cursor、Grok 或 OpenCode 后，这里会显示它们的用量。', 'Install Claude, Codex, Antigravity, Copilot, Cursor, Grok or OpenCode to see their usage here.'),
      }),
    ]);
  }
  const content = model.selected === API_TAB ? apiTab(model.api, s, t) : agentTab(model.agent(model.selected), s, t);
  return ui.column({ gap: s.gap, width: s.width }, [
    header,
    ui.tabs({
      tabs: tabs.map(tab => ({ id: tab.id, title: tab.percent === undefined ? tab.name : `${tab.name} ${Math.round(tab.percent)}%` })),
      selected: model.selected,
      onSelect: id => model.select(id),
    }, ui.column({ gap: s.sectionGap, margin: [s.gap, 0], key: `tab:${model.selected}` }, content)),
    s.compact ? null : ui.text(note, { variant: 'caption', color: 'muted' }),
  ]);
}

// ───────────────────────── an agent ─────────────────────────

function agentTab(row, s, t) {
  if (!row) return [];
  const children = [];
  const withData = row.snapshots.filter(e => e.updatedAt !== undefined || (e.snapshot?.windows ?? []).length > 0 || e.snapshot?.unavailable);
  // Account names matter as soon as the agent has more than one account (some may still be waiting).
  const several = withData.length + row.waiting.length > 1;
  withData.forEach((entry, index) => {
    if (index > 0 && s.divider) children.push(ui.divider());
    children.push(accountBlock(entry, several, row, s, t));
  });
  // Accounts that have not reported yet: one line naming them, not a block each.
  if (row.waiting.length > 0) {
    const names = row.waiting.map(w => w.label ?? w.account.slice(0, 6));
    const shown = names.length > 3 ? `${names.slice(0, 3).join('、')} ${t(`等 ${names.length} 个`, `and ${names.length - 3} more`)}` : names.join('、');
    children.push(ui.text(withData.length > 0
      ? t(`还没有数据：${shown}。用它们启动一次会话后显示。`, `No data yet: ${shown}. Start a session with them to see it.`)
      : t(`还没有用量：${shown}。用它启动一次会话后显示，或在设置 → 插件 → 智能体用量里改用自定义命令。`, `No usage yet: ${shown}. Start a session with it, or use a custom command in Settings → Plugins → Agent Usage.`),
    { variant: 'caption', color: 'muted', tooltip: names.join('\n') }));
  }
  if (withData.length === 0 && row.waiting.length === 0) {
    children.push(ui.text(t('等待数据…', 'Waiting for data…'), { variant: 'caption', color: 'muted' }));
  }
  if (row.error) children.push(ui.text(t(`查询失败：${row.error}`, `Query failed: ${row.error}`), { variant: 'caption', color: 'danger' }));
  return children;
}

function accountBlock(entry, several, row, s, t) {
  const { account, label, snapshot, updatedAt } = entry;
  const name = several ? (label ?? account.slice(0, 6)) : (label ?? row.name);
  const children = [header(name, {
    plan: snapshot?.plan ? capitalize(snapshot.plan) : undefined,
    tag: snapshot?.source === 'custom' ? t('自定义', 'Custom') : undefined,
    updatedAt,
  }, s)];
  for (const w of snapshot?.windows ?? []) children.push(...windowRows(w, s, t));
  if (snapshot?.credits) {
    children.push(factRow(t('余额', 'Credits'), snapshot.credits.unlimited ? t('不限', 'Unlimited') : String(snapshot.credits.balance ?? '—'), undefined, s));
  }
  if (snapshot?.unavailable && (snapshot.windows ?? []).length === 0) children.push(hintRow(snapshot.unavailable, t));
  return ui.column({ gap: s.rowGap, key: account || '_' }, children);
}

/** The first line of a block: its name, then plan, tag and age; badges when comfortable, plain captions when compact. */
function header(name, { plan, tag, tagColor, updatedAt, busy, trailing }, s) {
  const mark = (text, color) => (s.compact
    ? ui.text(text, { variant: 'caption', color: color === 'warning' ? 'warning' : 'secondary' })
    : ui.badge(text, { color: color ?? 'accent' }));
  return ui.row({ gap: s.cellGap, align: 'center' }, [
    ui.text(name, { variant: s.compact ? 'body' : 'heading', truncate: true, grow: 1, tooltip: name }),
    plan ? mark(plan) : null,
    tag ? mark(tag, tagColor ?? 'default') : null,
    busy ? ui.spinner() : updatedAt ? ui.relativeTime({ at: updatedAt }) : null,
    trailing ?? null,
  ]);
}

function hintRow(unavailable, t) {
  const action = unavailable.action;
  return ui.row({ gap: 2, align: 'center' }, [
    // grow: the hint takes the free width and wraps instead of running under the button.
    ui.text(unavailable.hint ?? t('拿不到额度数据。', 'No quota data.'), { variant: 'caption', color: 'muted', grow: 1 }),
    action ? ui.button({ label: action.label, variant: 'ghost', size: 'sm', onClick: () => (action.url ? shell.open(action.url) : commands.execute(action.command, ...(action.args ?? []))) }) : null,
  ]);
}

// ───────────────────────── a window ─────────────────────────

/**
 * One window on one line: name, bar, percentage (in the warning colors past 75% and 90%), when it resets. The tooltip
 * has the rest (how much of the window has passed, the reset's date). When the current pace runs the window out
 * before it resets, a second line says when.
 */
function windowRows(w, s, t) {
  const p = Math.round(w.usedPercent);
  const pace = paceOf(w);
  const rows = [ui.row({ gap: s.cellGap, align: 'center', key: w.id, tooltip: windowTooltip(w, pace, t) }, [
    ui.text(s.compact ? shortLabel(w) : w.label, { variant: 'caption', color: 'secondary', width: s.labelWidth, truncate: true }),
    ui.progress({ value: Math.min(1, w.usedPercent / 100), grow: 1, height: s.barHeight }),
    ui.text(`${p}%`, { width: s.percentWidth, align: 'end', color: p >= 90 ? 'danger' : p >= 75 ? 'warning' : 'default' }),
    ui.text(w.stale ? t('已重置', 'Reset') : w.resetsAt ? untilText(w.resetsAt, s, t) : '', { variant: 'caption', color: 'muted', width: s.resetWidth, align: 'end', truncate: true }),
  ])];
  if (pace?.runsOutAt) {
    rows.push(ui.row({ gap: s.cellGap, key: `${w.id}:pace` }, [
      ui.text('', { width: s.labelWidth }),
      ui.text(t(`按当前速度约 ${untilText(pace.runsOutAt, s, t)}后用完，早于重置`, `At this pace it runs out in ${untilText(pace.runsOutAt, s, t)}, before it resets`), { variant: 'caption', color: 'warning', grow: 1 }),
    ]));
  }
  return rows;
}

/**
 * How far into its window a reading is and, when it will run out before the reset at the current rate, when. Early in
 * a window or at low usage the rate says little, so no projection is made then.
 */
export function paceOf(w, now = Date.now()) {
  if (!w.durationMs || !w.resetsAt || w.stale) return undefined;
  const left = w.resetsAt - now;
  if (left <= 0 || left > w.durationMs) return undefined;
  const elapsed = w.durationMs - left;
  const share = elapsed / w.durationMs;
  const used = w.usedPercent / 100;
  let runsOutAt;
  if (share >= 0.1 && used >= 0.2 && used < 1) {
    const at = now + ((1 - used) * elapsed) / used;
    // Running out only minutes before the reset is not worth a line: the margin is 5% of the window, at least 10 minutes.
    if (at < w.resetsAt - Math.max(10 * 60_000, w.durationMs * 0.05)) runsOutAt = at;
  }
  return { share, runsOutAt };
}

function windowTooltip(w, pace, t) {
  const parts = [`${w.label} · ${t('已用', 'used')} ${Math.round(w.usedPercent)}%`];
  if (pace) parts.push(t(`时间已过 ${Math.round(pace.share * 100)}%`, `${Math.round(pace.share * 100)}% of the time passed`));
  if (w.stale) parts.push(t('已重置', 'has reset'));
  else if (w.resetsAt) parts.push(t(`${dateText(w.resetsAt)} 重置`, `resets ${dateText(w.resetsAt)}`));
  return parts.join(' · ');
}

/** "5h", "7d", "7d Opus": the window's length, short, with its qualifier; the full label when the length is unknown. */
function shortLabel(w) {
  const minutes = w.durationMs ? Math.round(w.durationMs / 60_000) : undefined;
  let text;
  if (minutes && minutes % 1440 === 0) text = `${minutes / 1440}d`;
  else if (minutes && minutes % 60 === 0) text = `${minutes / 60}h`;
  else if (minutes) text = `${minutes}m`;
  else return w.label;
  return w.tag ? `${text} ${w.tag}` : text;
}

// ───────────────────────── API providers ─────────────────────────

function apiTab(api, s, t) {
  const children = [];
  if (!api.available) {
    children.push(ui.text(t('这个插件没有联网权限，无法查询 API 余额。可以在设置 → 插件里开启。', 'This plugin may not use the network, so API balances cannot be queried. Allow it in Settings → Plugins.'), { variant: 'caption', color: 'muted' }));
    return children;
  }
  api.providers.forEach((p, index) => {
    if (index > 0 && s.divider) children.push(ui.divider());
    children.push(providerBlock(p, api, s, t));
  });
  if (api.providers.length === 0) {
    children.push(ui.text(t('添加 DeepSeek、Kimi、OpenRouter、硅基流动或智谱编程套餐的 API 密钥，这里会显示余额和额度。', 'Add an API key of DeepSeek, Kimi, OpenRouter, SiliconFlow or the GLM Coding Plan to see its balance and quota here.'), { variant: 'caption', color: 'muted' }));
  }
  if (api.addable.length > 0) {
    children.push(ui.row({ gap: s.cellGap, align: 'center' }, [
      ui.button({ label: t('添加 API 密钥', 'Add an API key'), icon: 'lucide:plus', variant: s.compact ? 'ghost' : 'secondary', size: 'sm', onClick: api.add }),
    ]));
  }
  return children;
}

function providerBlock(p, api, s, t) {
  const { snapshot, updatedAt, error, busy } = p;
  const menu = ui.menu({ items: [
    { id: 'console', label: t('打开控制台', 'Open console'), onSelect: () => shell.open(p.console) },
    { id: 'replace', label: t('更换密钥', 'Replace key'), onSelect: () => api.replace(p.id) },
    { id: 'remove', label: t('移除', 'Remove'), danger: true, onSelect: () => api.remove(p.id) },
  ] }, ui.iconButton({ icon: 'lucide:ellipsis', tooltip: t('更多', 'More') }));
  const children = [header(p.name, {
    plan: snapshot?.plan,
    tag: p.unofficial ? t('未公开接口', 'Undocumented') : undefined,
    tagColor: 'warning',
    updatedAt, busy, trailing: menu,
  }, s)];
  for (const w of snapshot?.windows ?? []) children.push(...windowRows(w, s, t));
  for (const b of snapshot?.balances ?? []) {
    children.push(factRow(b.label, money(b.amount, b.currency), b.detail, s, b.amount !== undefined && b.amount <= 0 ? 'danger' : 'default'));
  }
  if (snapshot && (snapshot.windows ?? []).length === 0 && (snapshot.balances ?? []).length === 0) {
    children.push(ui.text(t('这个密钥没有可显示的额度。', 'Nothing to show for this key.'), { variant: 'caption', color: 'muted' }));
  }
  if (error) children.push(ui.text(t(`查询失败：${error}`, `Query failed: ${error}`), { variant: 'caption', color: 'danger' }));
  return ui.column({ gap: s.rowGap, key: p.id }, children);
}

/** A label, a value and an optional detail on one line, aligned with the window rows. */
function factRow(label, value, detail, s, color = 'default') {
  return ui.row({ gap: s.cellGap, align: 'center' }, [
    ui.text(label, { variant: 'caption', color: 'secondary', width: s.labelWidth, truncate: true, tooltip: label }),
    ui.text(value, { color }),
    detail ? ui.text(detail, { variant: 'caption', color: 'muted', grow: 1, align: 'end', truncate: true, tooltip: detail }) : null,
  ]);
}

// ───────────────────────── formatting ─────────────────────────

/** How long until `ms`, short enough for its column: compact "45m" / "4h07" / "周四 09:00"; comfortable "4 小时 07 分". */
function untilText(ms, s, t) {
  const minutes = Math.max(0, Math.round((ms - Date.now()) / 60_000));
  if (minutes < 60) return s.compact ? `${minutes}m` : t(`${minutes} 分`, `${minutes}m`);
  if (minutes < 24 * 60) {
    const h = Math.floor(minutes / 60);
    const m = String(minutes % 60).padStart(2, '0');
    return s.compact ? `${h}h${m}` : t(`${h} 小时 ${m} 分`, `${h}h ${m}m`);
  }
  return dateText(ms);
}

function dateText(ms) {
  return new Date(ms).toLocaleString(env.language, { weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
}

function capitalize(text) {
  return typeof text === 'string' && text.length > 0 ? text[0].toUpperCase() + text.slice(1) : text;
}

export { API_TAB };
