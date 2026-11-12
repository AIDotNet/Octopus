// The indicator's popover (design/plugins/12 §8): one section per installed agent with its plan, windows with reset
// countdowns, balance, session cost and freshness; where a quota cannot be read, the reason and what to do instead.

import { commands, shell, ui } from 'octopus';

export function renderPopover({ rows, refresh, t, formatReset }) {
  const sections = [];
  rows.forEach((row, index) => {
    if (index > 0) sections.push(ui.divider());
    sections.push(agentSection(row, t, formatReset));
  });
  if (rows.length === 0) {
    sections.push(ui.emptyState({
      icon: 'lucide:gauge',
      title: t('没有发现智能体', 'No agents found'),
      description: t('安装 Claude、Codex、Cursor、Grok 或 OpenCode 后，这里会显示它们的用量。', 'Install Claude, Codex, Cursor, Grok or OpenCode to see their usage here.'),
    }));
  }
  return ui.column({ gap: 3, width: 380 }, [
    ui.row({ justify: 'between', align: 'center' }, [
      ui.text(t('智能体用量', 'Agent usage'), { variant: 'heading' }),
      ui.row({ gap: 1 }, [
        ui.iconButton({ icon: 'lucide:chart-column', tooltip: t('用量历史', 'Usage history'), command: 'octopus.agent-usage.history.open' }),
        ui.iconButton({ icon: 'lucide:refresh-cw', tooltip: t('刷新', 'Refresh'), onClick: refresh }),
      ]),
    ]),
    ...sections,
    ui.text(t('数据来自各智能体的官方接口，只在本机处理。', 'From each agent’s official interface, processed on this machine only.'), { variant: 'caption', color: 'muted' }),
  ]);
}

function agentSection(row, t, formatReset) {
  const children = [];
  const snapshots = row.snapshots.length > 0 ? row.snapshots : [{ account: '', snapshot: undefined, updatedAt: undefined }];
  for (const { account, label, snapshot, updatedAt } of snapshots) {
    const plan = snapshot?.plan ? ` · ${capitalize(snapshot.plan)}` : '';
    const custom = snapshot?.source === 'custom' ? t(' · 自定义来源', ' · custom source') : '';
    const accountLabel = account && row.snapshots.length > 1 ? ` · ${label ?? account.slice(0, 6)}` : '';
    children.push(ui.row({ justify: 'between', align: 'center' }, [
      ui.text(`${row.name}${plan}${accountLabel}${custom}`, { variant: 'heading' }),
      updatedAt ? ui.relativeTime({ at: updatedAt }) : ui.text(t('等待数据', 'Waiting for data'), { variant: 'caption', color: 'muted' }),
    ]));
    for (const w of snapshot?.windows ?? []) {
      children.push(ui.column({ gap: 1 }, [
        // The bar carries the window's name and the percentage; the reset time goes under it.
        ui.progress({ value: Math.min(1, w.usedPercent / 100), label: w.label }),
        ui.text(w.stale ? t('已重置', 'Reset') : w.resetsAt ? t(`${formatReset(w.resetsAt)}重置`, `Resets ${formatReset(w.resetsAt)}`) : '', { variant: 'caption', color: 'muted', align: 'end' }),
      ]));
    }
    if (snapshot?.credits) {
      const c = snapshot.credits;
      children.push(ui.text(c.unlimited ? t('余额：不限', 'Credits: unlimited') : t(`余额：${c.balance ?? '—'}`, `Credits: ${c.balance ?? '—'}`), { variant: 'caption' }));
    }
    const session = snapshot?.session;
    if (session && (session.costUsd !== undefined || session.tokens || session.contextPercent !== undefined)) {
      const parts = [];
      if (session.costUsd !== undefined) parts.push(`$${session.costUsd.toFixed(2)}`);
      if (session.tokens) parts.push(`${compact(session.tokens)} tokens`);
      if (session.contextPercent !== undefined) parts.push(t(`上下文 ${Math.round(session.contextPercent)}%`, `context ${Math.round(session.contextPercent)}%`));
      children.push(ui.text(`${t('本会话', 'This session')}  ${parts.join(' · ')}`, { variant: 'caption' }));
    }
    if (snapshot?.unavailable && (snapshot.windows ?? []).length === 0) {
      const action = snapshot.unavailable.action;
      children.push(ui.row({ gap: 2, align: 'center' }, [
        // grow: the hint takes the free width and wraps instead of running under the button.
        ui.text(snapshot.unavailable.hint ?? t('拿不到额度数据。', 'No quota data.'), { variant: 'caption', color: 'secondary', grow: 1 }),
        action ? ui.button({ label: action.label, variant: 'ghost', size: 'sm', onClick: () => (action.url ? shell.open(action.url) : commands.execute(action.command, ...(action.args ?? []))) }) : null,
      ]));
    }
  }
  if (row.error) children.push(ui.text(t(`查询失败：${row.error}`, `Query failed: ${row.error}`), { variant: 'caption', color: 'danger' }));
  return ui.column({ gap: 2, key: row.id }, children);
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
