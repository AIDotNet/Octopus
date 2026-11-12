// The usage history tab (design/plugins/12 §10): tokens per day, per agent and per model, from the local session
// records of Claude and Codex. Only time, model and token counts are read (fs.scanJsonLines projects them in the
// host); conversation text never reaches the plugin. Files are scanned incrementally and the result is kept for
// the rest of the session.

import { env, fs, permissions, ui } from 'octopus';

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);
const DAY = 24 * 60 * 60_000;
const MAX_DAYS = 30;

/** path → { size, mtime, offset, lastTotal, model } */
const files = new Map();
/** `${day}|${agent}|${model}` → { input, output, cache, total } */
const totals = new Map();
const seenRequests = new Set();
let scanning;
let progress = { done: 0, total: 0 };
let skipped = 0;
let lastScan;
const listeners = new Set();

const SOURCES = [
  {
    agent: 'claude',
    glob: 'projects/**/*.jsonl',
    where: { type: 'assistant' },
    select: ['timestamp', 'requestId', 'message.model', 'message.usage.input_tokens', 'message.usage.output_tokens',
      'message.usage.cache_creation_input_tokens', 'message.usage.cache_read_input_tokens'],
    add(file, [timestamp, requestId, model, input, output, cacheWrite, cacheRead]) {
      // Claude writes one line per content block of a reply, all with the same request id and usage.
      if (requestId) {
        if (seenRequests.has(requestId)) return;
        seenRequests.add(requestId);
      }
      const cache = (cacheWrite ?? 0) + (cacheRead ?? 0);
      record(timestamp, 'claude', model ?? 'unknown', input ?? 0, output ?? 0, cache);
    },
  },
  {
    agent: 'codex',
    glob: 'sessions/**/rollout-*.jsonl',
    where: { 'payload.type': 'token_count' },
    select: ['timestamp', 'payload.info.total_token_usage.input_tokens', 'payload.info.total_token_usage.output_tokens',
      'payload.info.total_token_usage.cached_input_tokens', 'payload.info.total_token_usage.total_tokens'],
    async prepare(file, path) {
      if (file.model !== undefined) return;
      const first = await fs.scanJsonLines(path, { where: { type: 'turn_context' }, select: ['payload.model'], limit: 1 });
      file.model = first.rows[0]?.[0] ?? 'unknown';
    },
    add(file, [timestamp, input, output, cached, total]) {
      // Codex reports running totals per session: the usage of a turn is the difference to the previous total.
      if (typeof total !== 'number') return;
      const last = file.lastTotal ?? { input: 0, output: 0, cached: 0, total: 0 };
      if (total < last.total) return;
      const delta = { input: (input ?? 0) - last.input, output: (output ?? 0) - last.output, cached: (cached ?? 0) - last.cached };
      file.lastTotal = { input: input ?? 0, output: output ?? 0, cached: cached ?? 0, total };
      if (delta.input + delta.output <= 0) return;
      record(timestamp, 'codex', file.model ?? 'unknown', Math.max(0, delta.input - delta.cached), delta.output, Math.max(0, delta.cached));
    },
  },
];

function record(timestamp, agent, model, input, output, cache) {
  const ms = Date.parse(timestamp);
  if (!Number.isFinite(ms) || ms < Date.now() - MAX_DAYS * DAY) return;
  const day = new Date(ms).toISOString().slice(0, 10);
  const key = `${day}|${agent}|${model}`;
  const row = totals.get(key) ?? { input: 0, output: 0, cache: 0, total: 0 };
  row.input += input;
  row.output += output;
  row.cache += cache;
  row.total += input + output + cache;
  totals.set(key, row);
}

/** The agent data roots granted to this plugin, from its file scopes. */
function roots(agent) {
  return fs.scopes().filter(s => s.kind === 'agentdata' && s.agent === agent).map(s => s.root);
}

function changed() {
  for (const listener of listeners) listener();
}

export function scan() {
  scanning ??= (async () => {
    try {
      const work = [];
      for (const source of SOURCES) {
        if (!permissions.has(`fs.agentData.${source.agent}`)) continue;
        for (const root of roots(source.agent)) {
          let listing;
          try {
            listing = await fs.statMany({ root, glob: source.glob });
          } catch {
            continue;
          }
          for (const entry of listing.entries) {
            if (entry.type !== 'file' || entry.mtime < Date.now() - MAX_DAYS * DAY) continue;
            const known = files.get(entry.path);
            if (known && known.size === entry.size && known.mtime === entry.mtime) continue;
            work.push({ source, entry });
          }
        }
      }
      progress = { done: 0, total: work.length };
      changed();
      for (const { source, entry } of work) {
        let file = files.get(entry.path);
        // A file that shrank was replaced: start over (its earlier rows stay counted; good enough for a chart).
        if (!file || entry.size < file.size) {
          file = { offset: 0 };
          files.set(entry.path, file);
        }
        try {
          await source.prepare?.(file, entry.path);
          for (;;) {
            const page = await fs.scanJsonLines(entry.path, { fromOffset: file.offset, where: source.where, select: source.select, limit: 5000 });
            for (const row of page.rows) source.add(file, row);
            skipped += page.skipped;
            file.offset = page.nextOffset;
            if (page.done || page.rows.length === 0) break;
          }
          file.size = entry.size;
          file.mtime = entry.mtime;
        } catch {
          // A file being written or removed while we read it: try again on the next scan.
        }
        progress = { done: progress.done + 1, total: progress.total };
        if (progress.done % 10 === 0) changed();
      }
      lastScan = Date.now();
    } finally {
      scanning = undefined;
      changed();
    }
  })();
  return scanning;
}

export class HistoryView {
  constructor(ctx) {
    this.ctx = ctx;
    this.groupBy = ctx.state?.groupBy ?? 'model';
    this.days = ctx.state?.days ?? 14;
    this.listener = () => ctx.invalidate();
    listeners.add(this.listener);
    if (!lastScan || Date.now() - lastScan > 5 * 60_000) scan();
  }

  getState() {
    return { groupBy: this.groupBy, days: this.days };
  }

  dispose() {
    listeners.delete(this.listener);
  }

  render() {
    const granted = SOURCES.filter(s => permissions.has(`fs.agentData.${s.agent}`));
    const header = ui.row({ justify: 'between', align: 'center' }, [
      ui.column({ gap: 0 }, [
        ui.text(t('用量历史', 'Usage history'), { variant: 'title' }),
        ui.text(t('仅本机记录：Claude 与 Codex 的会话记录里的 token 数', 'This machine only: token counts from Claude and Codex session records'), { variant: 'caption', color: 'muted' }),
      ]),
      ui.row({ gap: 2, align: 'center' }, [
        ui.segmented({
          value: String(this.days), options: [{ value: '7', label: t('7 天', '7 days') }, { value: '14', label: t('14 天', '14 days') }, { value: '30', label: t('30 天', '30 days') }],
          onChange: v => { this.days = Number(v); this.ctx.saveState(); this.ctx.invalidate(); },
        }),
        ui.segmented({
          value: this.groupBy, options: [{ value: 'model', label: t('按模型', 'By model') }, { value: 'agent', label: t('按智能体', 'By agent') }],
          onChange: v => { this.groupBy = v; this.ctx.saveState(); this.ctx.invalidate(); },
        }),
        ui.button({ label: t('重新扫描', 'Rescan'), icon: 'lucide:refresh-cw', loading: !!scanning, onClick: () => scan() }),
      ]),
    ]);

    if (granted.length === 0) {
      return ui.column({ gap: 4, padding: 6 }, [header, ui.alert({
        kind: 'info',
        title: t('没有读取会话记录的权限', 'No access to session records'),
        description: t('在 设置 → 插件 → 智能体用量 里打开“读取 claude/codex 的本地数据”。', 'Turn on “Local data of claude/codex” in Settings → Plugins → Agent Usage.'),
      })]);
    }

    const body = [header];
    if (scanning) {
      body.push(ui.progress({ value: progress.total ? progress.done / progress.total : 0, label: t(`扫描中 ${progress.done}/${progress.total} 个文件`, `Scanning ${progress.done}/${progress.total} files`) }));
    }
    const from = Date.now() - this.days * DAY;
    const days = [];
    for (let d = this.days - 1; d >= 0; d--) days.push(new Date(Date.now() - d * DAY).toISOString().slice(0, 10));
    const perDay = new Array(days.length).fill(0);
    const groups = new Map();
    let sum = 0;
    for (const [key, row] of totals) {
      const [day, agent, model] = key.split('|');
      const index = days.indexOf(day);
      if (index < 0) continue;
      perDay[index] += row.total;
      const group = this.groupBy === 'agent' ? agent : `${agent}|${model}`;
      const g = groups.get(group) ?? { agent, model: this.groupBy === 'agent' ? undefined : model, input: 0, output: 0, cache: 0, total: 0 };
      g.input += row.input; g.output += row.output; g.cache += row.cache; g.total += row.total;
      groups.set(group, g);
      sum += row.total;
    }

    if (sum === 0 && !scanning) {
      body.push(ui.emptyState({ icon: 'lucide:chart-column', title: t('这段时间没有记录', 'No records in this period'), description: t('用 Claude 或 Codex 工作后，这里会按天显示 token 用量。', 'After working with Claude or Codex, daily token usage shows here.') }));
    } else {
      body.push(ui.stat({ label: t(`${this.days} 天合计`, `${this.days}-day total`), value: compact(sum), detail: 'tokens' }));
      // Arc's bar chart reads the daily average in its headline at rest and the hovered day while exploring.
      body.push(ui.card({ title: t('每天的 token', 'Tokens per day') }, [
        ui.barChart({
          categories: days.map(d => d.slice(5)), series: [{ name: 'tokens', values: perDay }], plotHeight: 220,
          label: 'tokens', averageLabel: t('日均', 'Daily average'), valueLabel: t('当天', 'That day'), compact: true,
          period: `${days[0]} – ${days[days.length - 1]}`,
        }),
      ]));
      const byAgent = this.groupBy === 'agent';
      // A model with nothing counted (Claude's "<synthetic>" replies) is left out.
      const rows = [...groups.values()].filter(g => g.total > 0).sort((a, b) => b.total - a.total).map(g => ({
        id: `${g.agent}|${g.model ?? ''}`,
        cells: byAgent ? [g.agent, g.input, g.output, g.cache, g.total] : [g.model, g.agent, g.input, g.output, g.cache, g.total],
      }));
      const columns = [
        ...(byAgent ? [] : [{ id: 'model', title: t('模型', 'Model') }]),
        { id: 'agent', title: t('智能体', 'Agent') },
      ];
      body.push(ui.card({ title: byAgent ? t('按智能体', 'By agent') : t('按模型', 'By model') }, [
        ui.table({
          key: this.groupBy,
          columns: [
            ...columns,
            { id: 'input', title: t('输入', 'Input'), type: 'number', sortable: true, format: 'compact' },
            { id: 'output', title: t('输出', 'Output'), type: 'number', sortable: true, format: 'compact' },
            { id: 'cache', title: t('缓存', 'Cache'), type: 'number', sortable: true, format: 'compact' },
            { id: 'total', title: t('合计', 'Total'), type: 'number', sortable: true, format: 'compact' },
          ],
          rows,
        }),
      ]));
    }
    if (skipped > 0) body.push(ui.text(t(`有 ${skipped} 行格式无法识别，已跳过。`, `${skipped} lines in an unknown format were skipped.`), { variant: 'caption', color: 'muted' }));
    return ui.scroll({}, ui.column({ gap: 4, padding: 6 }, body));
  }
}

/** 1,010,000 → "1.01M": at most two decimals, trailing zeros dropped. */
function compact(n) {
  const units = [[1e12, 'T'], [1e9, 'B'], [1e6, 'M'], [1e3, 'K']];
  for (const [size, unit] of units) {
    if (Math.abs(n) >= size) return `${Number((n / size).toFixed(2))}${unit}`;
  }
  return String(Math.round(n));
}
