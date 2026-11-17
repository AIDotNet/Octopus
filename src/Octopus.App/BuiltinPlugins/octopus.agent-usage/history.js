// The usage history tab (design/plugins/12 §10): tokens per day, per agent and per model, from the local session
// records of Claude and Codex. Only time, model and token counts are read (fs.scanJsonLines projects them in the
// host); conversation text never reaches the plugin. Files are scanned incrementally and the result is kept for
// the rest of the session.

import { env, fs, permissions, ui } from 'octopus';
import { costOf, ensurePrices, priceSource } from './pricing.js';
import { compact } from './bill.js';
import { openBillExport } from './export.js';

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);
const DAY = 24 * 60 * 60_000;
// The tab charts up to 30 days; the bill export reaches back as far as this (a quarter, or a month and a half more).
const MAX_DAYS = 92;

/** path → { size, mtime, offset, lastTotal, model } */
const files = new Map();
/** `${day}|${agent}|${model}` → { requests, input, output, cache, total, cost, unpriced } (cost in USD at API list prices) */
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
    modelOf: (file, row) => row[2],
    select: ['timestamp', 'requestId', 'message.model', 'message.usage.input_tokens', 'message.usage.output_tokens',
      'message.usage.cache_creation_input_tokens', 'message.usage.cache_read_input_tokens',
      'message.usage.cache_creation.ephemeral_1h_input_tokens'],
    add(file, [timestamp, requestId, model, input, output, cacheWrite, cacheRead, write1h]) {
      // Claude writes one line per content block of a reply, all with the same request id and usage.
      if (requestId) {
        if (seenRequests.has(requestId)) return;
        seenRequests.add(requestId);
      }
      // Cache writes cost more for the 1-hour cache than for the 5-minute one; without the split, all are 5-minute.
      const oneHour = Math.min(write1h ?? 0, cacheWrite ?? 0);
      record(timestamp, 'claude', model ?? 'unknown', {
        input: input ?? 0, output: output ?? 0, cacheRead: cacheRead ?? 0, cacheWrite5m: (cacheWrite ?? 0) - oneHour, cacheWrite1h: oneHour,
      });
    },
  },
  {
    agent: 'codex',
    glob: 'sessions/**/rollout-*.jsonl',
    where: { 'payload.type': 'token_count' },
    modelOf: file => file.model,
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
      record(timestamp, 'codex', file.model ?? 'unknown', { input: Math.max(0, delta.input - delta.cached), output: delta.output, cacheRead: Math.max(0, delta.cached) });
    },
  },
];

function record(timestamp, agent, model, tokens) {
  const ms = Date.parse(timestamp);
  if (!Number.isFinite(ms) || ms < Date.now() - MAX_DAYS * DAY) return;
  const key = `${localDay(ms)}|${agent}|${model}`;
  const row = totals.get(key) ?? { requests: 0, input: 0, output: 0, cache: 0, total: 0, cost: 0, unpriced: 0 };
  const { input = 0, output = 0, cacheRead = 0, cacheWrite5m = 0, cacheWrite1h = 0 } = tokens;
  const cache = cacheRead + cacheWrite5m + cacheWrite1h;
  row.requests += 1;
  row.input += input;
  row.output += output;
  row.cache += cache;
  row.total += input + output + cache;
  const cost = costOf(model, tokens);
  if (cost === undefined) row.unpriced += input + output + cache;
  else row.cost += cost;
  totals.set(key, row);
}

/** The day of a moment on this machine's calendar ("2026-11-15"): a reply at 08:00 in Sydney belongs to that day, not to UTC's day before. */
function localDay(ms) {
  const d = new Date(ms);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** The last `count` days, oldest first, ending today. */
function lastDays(count) {
  const days = [];
  const today = new Date();
  for (let d = count - 1; d >= 0; d--) days.push(localDay(new Date(today.getFullYear(), today.getMonth(), today.getDate() - d, 12).getTime()));
  return days;
}

/** Every day, agent and model counted so far, as the bill export reads them (see bill.js). */
export function usageRows() {
  return [...totals].map(([key, row]) => {
    const [day, agent, model] = key.split('|');
    return { day, agent, model, requests: row.requests, input: row.input, output: row.output, cache: row.cache, cost: row.cost, unpriced: row.unpriced };
  });
}

/**
 * Estimated cost per agent today and over the last 7 days, for the popover. Empty until a scan has run; `ensureScanned`
 * starts one when the last is older than five minutes.
 */
export function costSummary() {
  const days = lastDays(7);
  const today = days[days.length - 1];
  const result = new Map();
  for (const [key, row] of totals) {
    const [day, agent] = key.split('|');
    if (!days.includes(day)) continue;
    const entry = result.get(agent) ?? { today: 0, week: 0 };
    entry.week += row.cost;
    if (day === today) entry.today += row.cost;
    result.set(agent, entry);
  }
  return result;
}

/** New prices change every cost already counted: forget the totals, so the next scan reads every record again. */
function forget() {
  files.clear();
  totals.clear();
  seenRequests.clear();
  skipped = 0;
}

export function ensureScanned() {
  if (SOURCES.some(s => permissions.has(`fs.agentData.${s.agent}`)) && !scanning && (!lastScan || Date.now() - lastScan > 5 * 60_000)) scan();
}

export function onChange(listener) {
  listeners.add(listener);
  return { dispose: () => listeners.delete(listener) };
}

/** The agent data roots granted to this plugin, from its file scopes. */
function roots(agent) {
  return fs.scopes().filter(s => s.kind === 'agentdata' && s.agent === agent).map(s => s.root);
}

function changed() {
  for (const listener of listeners) listener();
}

/** Reads what is new in the session records; `refreshPrices` also asks the host to sync the prices again. */
export function scan({ refreshPrices = false } = {}) {
  scanning ??= (async () => {
    let repriced = false;
    try {
      // Costs are counted as the rows are read: prices synced since the last scan mean counting everything again.
      if (await ensurePrices([], { check: true, refresh: refreshPrices })) forget();
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
            // The prices of the models on this page, before their rows are counted.
            if (await ensurePrices(page.rows.map(row => source.modelOf(file, row)))) repriced = true;
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
    // The host synced new prices in the middle of the scan: part was counted with the old ones. Count it all again.
    if (repriced) {
      forget();
      scan();
    }
  })();
  return scanning;
}

export class HistoryView {
  constructor(ctx) {
    this.ctx = ctx;
    this.groupBy = ctx.state?.groupBy ?? 'model';
    this.days = ctx.state?.days ?? 14;
    this.metric = ctx.state?.metric ?? 'cost';
    this.listener = () => ctx.invalidate();
    listeners.add(this.listener);
    if (!lastScan || Date.now() - lastScan > 5 * 60_000) scan();
  }

  getState() {
    return { groupBy: this.groupBy, days: this.days, metric: this.metric };
  }

  dispose() {
    listeners.delete(this.listener);
  }

  set(field, value) {
    this[field] = value;
    this.ctx.saveState();
    this.ctx.invalidate();
  }

  render() {
    const granted = SOURCES.filter(s => permissions.has(`fs.agentData.${s.agent}`));
    const header = ui.row({ justify: 'between', align: 'center' }, [
      ui.column({ gap: 0 }, [
        ui.text(t('用量历史', 'Usage history'), { variant: 'title' }),
        ui.text(t('仅本机记录：Claude 与 Codex 会话记录里的 token 数；费用按 API 公开价格估算', 'This machine only: token counts from Claude and Codex session records; cost estimated at public API prices'), { variant: 'caption', color: 'muted' }),
      ]),
      ui.row({ gap: 2, align: 'center' }, [
        ui.segmented({
          value: String(this.days), options: [{ value: '7', label: t('7 天', '7 days') }, { value: '14', label: t('14 天', '14 days') }, { value: '30', label: t('30 天', '30 days') }],
          onChange: v => this.set('days', Number(v)),
        }),
        ui.segmented({
          value: this.groupBy, options: [{ value: 'model', label: t('按模型', 'By model') }, { value: 'agent', label: t('按智能体', 'By agent') }],
          onChange: v => this.set('groupBy', v),
        }),
        // Rescan also has the host sync the prices from models.dev; changed prices recount everything.
        ui.button({ label: t('导出账单', 'Export bill'), icon: 'lucide:receipt-text', onClick: () => openBillExport(usageRows) }),
        ui.button({ label: t('重新扫描', 'Rescan'), icon: 'lucide:refresh-cw', loading: !!scanning, onClick: () => scan({ refreshPrices: true }) }),
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
    const days = lastDays(this.days);
    const today = days[days.length - 1];
    const tokensPerDay = new Array(days.length).fill(0);
    const costPerDay = new Array(days.length).fill(0);
    const groups = new Map();
    const unpricedModels = new Set();
    let sum = 0;
    let cost = 0;
    let unpriced = 0;
    for (const [key, row] of totals) {
      const [day, agent, model] = key.split('|');
      const index = days.indexOf(day);
      if (index < 0) continue;
      tokensPerDay[index] += row.total;
      costPerDay[index] += row.cost;
      const group = this.groupBy === 'agent' ? agent : `${agent}|${model}`;
      const g = groups.get(group) ?? { agent, model: this.groupBy === 'agent' ? undefined : model, input: 0, output: 0, cache: 0, total: 0, cost: 0, unpriced: 0 };
      g.input += row.input; g.output += row.output; g.cache += row.cache; g.total += row.total; g.cost += row.cost; g.unpriced += row.unpriced;
      groups.set(group, g);
      sum += row.total;
      cost += row.cost;
      unpriced += row.unpriced;
      if (row.unpriced > 0) unpricedModels.add(model);
    }

    if (sum === 0 && !scanning) {
      body.push(ui.emptyState({ icon: 'lucide:chart-column', title: t('这段时间没有记录', 'No records in this period'), description: t('用 Claude 或 Codex 工作后，这里会按天显示 token 用量和费用。', 'After working with Claude or Codex, daily tokens and cost show here.') }));
    } else {
      const activeDays = costPerDay.filter(c => c > 0).length;
      const todayIndex = days.indexOf(today);
      // Four figures in one row: what the period cost, what a working day costs, today, and the tokens behind it.
      body.push(ui.grid({ columns: 4, gap: 3 }, [
        ui.card({}, [ui.stat({ label: t(`${this.days} 天费用（估算）`, `${this.days}-day cost (est.)`), value: usd(cost), detail: unpriced > 0 ? t(`另有 ${compact(unpriced)} tokens 无价格`, `plus ${compact(unpriced)} tokens without a price`) : undefined })]),
        ui.card({}, [ui.stat({ label: t('活跃日均费用', 'Per active day'), value: usd(activeDays ? cost / activeDays : 0), detail: t(`${activeDays} 天有用量`, `${activeDays} active days`) })]),
        ui.card({}, [ui.stat({ label: t('今天', 'Today'), value: usd(costPerDay[todayIndex] ?? 0), detail: `${compact(tokensPerDay[todayIndex] ?? 0)} tokens` })]),
        ui.card({}, [ui.stat({ label: t(`${this.days} 天 token`, `${this.days}-day tokens`), value: compact(sum), detail: t(`缓存占 ${percent(sumOf(groups, 'cache'), sum)}`, `${percent(sumOf(groups, 'cache'), sum)} from cache`) })]),
      ]));
      const byCost = this.metric === 'cost';
      // Arc's bar chart reads the daily average in its headline at rest and the hovered day while exploring.
      body.push(ui.card({}, [
        ui.row({ justify: 'between', align: 'center' }, [
          ui.text(byCost ? t('每天的费用（美元，估算）', 'Cost per day (USD, est.)') : t('每天的 token', 'Tokens per day'), { variant: 'heading' }),
          ui.segmented({
            value: this.metric, options: [{ value: 'cost', label: t('费用', 'Cost') }, { value: 'tokens', label: 'Token' }],
            onChange: v => this.set('metric', v),
          }),
        ]),
        ui.barChart({
          key: this.metric,
          categories: days.map(d => d.slice(5)), series: [{ name: byCost ? 'USD' : 'tokens', values: byCost ? costPerDay.map(round2) : tokensPerDay }], plotHeight: 200,
          label: byCost ? 'USD' : 'tokens', unit: byCost ? 'USD' : '', averageLabel: t('日均', 'Daily average'), valueLabel: t('当天', 'That day'), compact: true,
          period: `${days[0]} – ${days[days.length - 1]}`,
        }),
      ]));
      const byAgent = this.groupBy === 'agent';
      // A model with nothing counted (Claude's "<synthetic>" replies) is left out. Cost first: it is what the table is for.
      const rows = [...groups.values()].filter(g => g.total > 0).sort((a, b) => b.cost - a.cost || b.total - a.total).map(g => ({
        id: `${g.agent}|${g.model ?? ''}`,
        cells: [
          ...(byAgent ? [] : [g.model]), g.agent,
          g.unpriced > 0 && g.cost === 0 ? '—' : round2(g.cost), percent(g.cost, cost), g.input, g.output, g.cache, g.total,
        ],
      }));
      body.push(ui.card({ title: byAgent ? t('按智能体', 'By agent') : t('按模型', 'By model') }, [
        ui.table({
          key: this.groupBy,
          columns: [
            ...(byAgent ? [] : [{ id: 'model', title: t('模型', 'Model') }]),
            { id: 'agent', title: t('智能体', 'Agent') },
            { id: 'cost', title: t('费用', 'Cost'), type: 'number', sortable: true, format: 'usd' },
            { id: 'share', title: t('占比', 'Share'), align: 'end' },
            { id: 'input', title: t('输入', 'Input'), type: 'number', sortable: true, format: 'compact' },
            { id: 'output', title: t('输出', 'Output'), type: 'number', sortable: true, format: 'compact' },
            { id: 'cache', title: t('缓存', 'Cache'), type: 'number', sortable: true, format: 'compact' },
            { id: 'total', title: t('合计', 'Total'), type: 'number', sortable: true, format: 'compact' },
          ],
          rows,
        }),
      ]));
    }
    const notes = [];
    if (unpricedModels.size > 0) notes.push(t(`没有价格、未计入费用的模型：${[...unpricedModels].join('、')}。`, `Models without a price, left out of the cost: ${[...unpricedModels].join(', ')}.`));
    notes.push(t('费用是按 API 公开价格推算的估算值；订阅不按 token 计费，这里表示同样的用量走 API 要花多少。', 'Cost is an estimate at public API prices; a subscription is not billed per token, so for one this is what the same usage would cost through the API.'));
    notes.push(priceNote());
    if (skipped > 0) notes.push(t(`有 ${skipped} 行格式无法识别，已跳过。`, `${skipped} lines in an unknown format were skipped.`));
    for (const note of notes) body.push(ui.text(note, { variant: 'caption', color: 'muted' }));
    return ui.scroll({}, ui.column({ gap: 4, padding: 6 }, body));
  }
}

/** Where the prices come from: models.dev and when, or why the built-in table is used. */
function priceNote() {
  const source = priceSource();
  if (source.syncedAt) {
    const when = new Date(source.syncedAt).toLocaleString(env.language, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
    const failed = source.error ? t(`（最近一次更新失败：${source.error}）`, ` (the last update failed: ${source.error})`) : '';
    return t(`价格来自 models.dev，${source.count} 个模型，${when} 同步${failed}。`, `Prices from models.dev, ${source.count} models, synced ${when}${failed}.`);
  }
  if (source.syncing) return t('正在从 models.dev 同步价格，暂用内置价格表（只含 Claude 和 GPT-6.1 Sol）。', 'Syncing prices from models.dev; the built-in table (Claude and GPT-6.1 Sol only) is used meanwhile.');
  return t(`无法从 models.dev 同步价格${source.error ? `（${source.error}）` : ''}，暂用内置价格表（只含 Claude 和 GPT-6.1 Sol）。`, `Prices could not be synced from models.dev${source.error ? ` (${source.error})` : ''}; the built-in table (Claude and GPT-6.1 Sol only) is used.`);
}

function sumOf(groups, field) {
  let total = 0;
  for (const g of groups.values()) total += g[field];
  return total;
}

function percent(part, whole) {
  return whole > 0 ? `${Math.round((part / whole) * 100)}%` : '—';
}

const round2 = n => Math.round(n * 100) / 100;

/** $1,234.56; under a dollar keeps two decimals too ($0.42). */
export function usd(n) {
  return `$${(Number.isFinite(n) ? n : 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}
