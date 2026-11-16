// The indicator's popover (design/plugins/12 §8): one section per installed agent with its plan, windows with reset
// countdowns, balance, session cost and freshness; where a quota cannot be read, the reason and what to do instead.
// Dense on purpose: one line per window (name, bar, percentage, reset), the agent's estimated cost on its header line.
// The host scrolls the popover when it is taller than its cap.

import { commands, env, shell, ui } from 'octopus';
import { usd } from './history.js';

export function renderPopover({ rows, costs, refresh, t }) {
  const sections = [];
  rows.forEach((row, index) => {
    if (index > 0) sections.push(ui.divider());
    sections.push(agentSection(row, costs?.get(row.id), t));
  });
  if (rows.length === 0) {
    sections.push(ui.emptyState({
      icon: 'lucide:gauge',
      title: t('没有发现智能体', 'No agents found'),
      description: t('安装 Claude、Codex、Cursor、Grok 或 OpenCode 后，这里会显示它们的用量。', 'Install Claude, Codex, Cursor, Grok or OpenCode to see their usage here.'),
    }));
  }
  let today = 0;
  for (const c of costs?.values() ?? []) today += c.today;
  return ui.column({ gap: 2, width: 380 }, [
    ui.row({ gap: 2, align: 'center' }, [
      ui.text(t('智能体用量', 'Agent usage'), { variant: 'heading', grow: 1 }),
      today > 0 ? ui.text(t(`今天 ${usd(today)}`, `Today ${usd(today)}`), { variant: 'caption', color: 'secondary', tooltip: t('按 API 公开价格估算的今日费用（全部智能体）', 'Today’s cost of all agents, estimated at public API prices') }) : null,
      ui.iconButton({ icon: 'lucide:chart-column', tooltip: t('用量历史', 'Usage history'), command: 'octopus.agent-usage.history.open' }),
      ui.iconButton({ icon: 'lucide:refresh-cw', tooltip: t('刷新', 'Refresh'), onClick: refresh }),
    ]),
    ...sections,
    ui.text(t('额度来自各智能体的官方接口；费用按 API 公开价格估算。只在本机处理。', 'Quotas from each agent’s official interface; cost estimated at public API prices. Processed on this machine only.'), { variant: 'caption', color: 'muted' }),
  ]);
}

function agentSection(row, cost, t) {
  const children = [];
  const snapshots = row.snapshots.length > 0 ? row.snapshots : [{ account: '', snapshot: undefined, updatedAt: undefined }];
  const several = snapshots.length > 1;
  const only = several ? undefined : snapshots[0];
  // Header: the agent (with the plan when there is one account), its cost, and how fresh a single account's data is.
  const title = only?.snapshot?.plan ? `${row.name} · ${capitalize(only.snapshot.plan)}` : row.name;
  const costText = cost && cost.week > 0 ? t(`今天 ${usd(cost.today)} · 7 天 ${usd(cost.week)}`, `Today ${usd(cost.today)} · 7 d ${usd(cost.week)}`) : null;
  children.push(ui.row({ gap: 2, align: 'center' }, [
    ui.text(title, { variant: 'heading', truncate: true, grow: 1 }),
    costText ? ui.text(costText, { variant: 'caption', color: 'secondary', tooltip: t('按 API 公开价格估算（本机会话记录）', 'Estimated at public API prices (this machine’s session records)') }) : null,
    only ? freshness(only, t) : null,
  ]));

  for (const entry of snapshots) {
    const { account, label, snapshot } = entry;
    if (several) {
      const plan = snapshot?.plan ? ` · ${capitalize(snapshot.plan)}` : '';
      children.push(ui.row({ gap: 2, align: 'center', margin: [1, 0] }, [
        ui.text(`${label ?? account.slice(0, 6)}${plan}`, { variant: 'caption', color: 'secondary', truncate: true, grow: 1 }),
        freshness(entry, t),
      ]));
    }
    for (const w of snapshot?.windows ?? []) children.push(windowRow(w, t));

    // Credits, session cost, tokens and context share one caption line.
    const facts = [];
    if (snapshot?.source === 'custom') facts.push(t('自定义来源', 'Custom source'));
    if (snapshot?.credits) facts.push(snapshot.credits.unlimited ? t('余额不限', 'Unlimited credits') : t(`余额 ${snapshot.credits.balance ?? '—'}`, `Credits ${snapshot.credits.balance ?? '—'}`));
    const session = snapshot?.session;
    if (session?.costUsd !== undefined) facts.push(t(`本会话 ${usd(session.costUsd)}`, `Session ${usd(session.costUsd)}`));
    if (session?.tokens) facts.push(`${compact(session.tokens)} tokens`);
    if (session?.contextPercent !== undefined) facts.push(t(`上下文 ${Math.round(session.contextPercent)}%`, `Context ${Math.round(session.contextPercent)}%`));
    if (facts.length > 0) children.push(ui.text(facts.join(' · '), { variant: 'caption', color: 'secondary' }));

    if (snapshot?.unavailable && (snapshot.windows ?? []).length === 0) {
      const action = snapshot.unavailable.action;
      children.push(ui.row({ gap: 2, align: 'center' }, [
        // grow: the hint takes the free width and wraps instead of running under the button.
        ui.text(snapshot.unavailable.hint ?? t('拿不到额度数据。', 'No quota data.'), { variant: 'caption', color: 'muted', grow: 1 }),
        action ? ui.button({ label: action.label, variant: 'ghost', size: 'sm', onClick: () => (action.url ? shell.open(action.url) : commands.execute(action.command, ...(action.args ?? []))) }) : null,
      ]));
    }
  }
  if (row.error) children.push(ui.text(t(`查询失败：${row.error}`, `Query failed: ${row.error}`), { variant: 'caption', color: 'danger' }));
  return ui.column({ gap: 1, key: row.id }, children);
}

/** One window on one line: name, bar, percentage (in the warning colors past 75% and 90%), when it resets. */
function windowRow(w, t) {
  const p = Math.round(w.usedPercent);
  return ui.row({ gap: 2, align: 'center', key: w.id }, [
    ui.text(w.label, { variant: 'caption', color: 'secondary', width: 64, truncate: true }),
    ui.progress({ value: Math.min(1, w.usedPercent / 100), grow: 1 }),
    ui.text(`${p}%`, { width: 36, align: 'end', color: p >= 90 ? 'danger' : p >= 75 ? 'warning' : 'default' }),
    ui.text(w.stale ? t('已重置', 'Reset') : w.resetsAt ? shortReset(w.resetsAt, t) : '', { variant: 'caption', color: 'muted', width: 72, align: 'end', truncate: true }),
  ]);
}

function freshness({ updatedAt }, t) {
  return updatedAt ? ui.relativeTime({ at: updatedAt }) : ui.text(t('等待数据', 'Waiting'), { variant: 'caption', color: 'muted' });
}

/** The reset time short enough for its column: "45 分" / "3 小时 05 分" / "周四 09:00". */
function shortReset(ms, t) {
  const minutes = Math.max(0, Math.round((ms - Date.now()) / 60_000));
  if (minutes < 60) return t(`${minutes} 分`, `${minutes}m`);
  if (minutes < 24 * 60) {
    const m = String(minutes % 60).padStart(2, '0');
    return t(`${Math.floor(minutes / 60)} 小时 ${m} 分`, `${Math.floor(minutes / 60)}h ${m}m`);
  }
  return new Date(ms).toLocaleString(env.language, { weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
}

function capitalize(s) {
  return typeof s === 'string' && s.length > 0 ? s[0].toUpperCase() + s.slice(1) : s;
}

/** 1,010,000 → "1.01M": at most two decimals, trailing zeros dropped. */
export function compact(n) {
  const units = [[1e12, 'T'], [1e9, 'B'], [1e6, 'M'], [1e3, 'K']];
  for (const [size, unit] of units) {
    if (Math.abs(n) >= size) return `${Number((n / size).toFixed(2))}${unit}`;
  }
  return String(Math.round(n));
}
