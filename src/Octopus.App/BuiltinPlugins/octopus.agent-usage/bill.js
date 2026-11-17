// The usage bill (issue #5): the usage history of a period and a choice of models, laid out as a till receipt (HTML,
// printable) or as a CSV of per-day rows. Pure functions on the history's rows; no host access, so it is easy to test.
//
// A row is one day of one model of one agent:
//   { day: '2026-11-17', agent, model, requests, input, output, cache, cost, unpriced }
// `cost` is the estimate in USD at public API prices; `unpriced` counts the tokens of models without a price, which
// the cost leaves out.

/** 1,010,000 → "1.01M": at most two decimals, trailing zeros dropped. */
export function compact(n) {
  const units = [[1e12, 'T'], [1e9, 'B'], [1e6, 'M'], [1e3, 'K']];
  for (const [size, unit] of units) {
    if (Math.abs(n) >= size) return `${Number((n / size).toFixed(2))}${unit}`;
  }
  return String(Math.round(n));
}

/** $0.0243, $12.30, $0: small amounts keep four decimals (a few cheap replies cost fractions of a cent). */
export function money(n) {
  if (!Number.isFinite(n) || n === 0) return '$0';
  if (Math.abs(n) < 1) return `$${n.toFixed(4)}`;
  return `$${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

/** Checks "YYYY-MM-DD" is a real calendar day. */
export function isDay(text) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(text ?? '').trim());
  if (!m) return false;
  const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
  return d.getFullYear() === Number(m[1]) && d.getMonth() === Number(m[2]) - 1 && d.getDate() === Number(m[3]);
}

/**
 * What the bill adds up: the rows from `from` to `to` (both inclusive, "YYYY-MM-DD" on this machine's calendar) whose
 * model is in `models` (a Set of `${agent}|${model}`; omitted means every model), grouped by agent, then by model.
 * Days compare as text, which is right for zero-padded ISO dates.
 */
export function buildBill(rows, { from, to, models } = {}) {
  const groups = new Map();
  const total = { requests: 0, input: 0, output: 0, cache: 0, cost: 0, unpriced: 0 };
  const days = new Set();
  for (const row of rows) {
    if (from && row.day < from) continue;
    if (to && row.day > to) continue;
    if (models && !models.has(`${row.agent}|${row.model}`)) continue;
    if (row.input + row.output + row.cache === 0) continue; // Claude's "<synthetic>" replies and the like
    days.add(row.day);
    let group = groups.get(row.agent);
    if (!group) groups.set(row.agent, group = { agent: row.agent, models: new Map(), requests: 0, cost: 0, unpriced: 0 });
    let m = group.models.get(row.model);
    if (!m) group.models.set(row.model, m = { model: row.model, requests: 0, input: 0, output: 0, cache: 0, cost: 0, unpriced: 0 });
    for (const target of [m, group, total]) {
      target.requests += row.requests;
      target.cost += row.cost;
      target.unpriced += row.unpriced;
    }
    for (const target of [m, total]) {
      target.input += row.input;
      target.output += row.output;
      target.cache += row.cache;
    }
  }
  const byCost = (a, b) => b.cost - a.cost || b.requests - a.requests;
  return {
    from, to,
    activeDays: days.size,
    total,
    groups: [...groups.values()].map(g => ({ ...g, models: [...g.models.values()].sort(byCost) })).sort(byCost),
  };
}

const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

/** "OC-20261117" for one day, "OC-20261101-20261117" for a period. */
export function billNumber({ from, to }) {
  const a = String(from).replaceAll('-', '');
  const b = String(to).replaceAll('-', '');
  return a === b ? `OC-${a}` : `OC-${a}-${b}`;
}

/**
 * A strip of bars under the total, as on a till receipt. It is decoration drawn from the bill number, not a code
 * any scanner reads.
 */
function barcode(text) {
  let x = 0;
  let seed = 7;
  const bars = [];
  for (const ch of text) seed = (seed * 31 + ch.charCodeAt(0)) >>> 0;
  for (let i = 0; i < 60; i++) {
    seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
    const width = 1 + ((seed >>> 16) % 3);
    if (i % 2 === 0) bars.push(`<rect x="${x}" y="0" width="${width}" height="40"/>`);
    x += width;
  }
  return `<svg class="bars" viewBox="0 0 ${x} 40" preserveAspectRatio="none" aria-hidden="true">${bars.join('')}</svg>`;
}

const LABELS = {
  zh: {
    subtitle: '本地 AI 智能体 · 用量账单', period: '区间', issued: '生成时间', number: '单号', currency: '币种', scope: '范围',
    allModels: '全部模型', someModels: n => `${n} 个模型`, calls: '次', inLabel: '入', outLabel: '出', cacheLabel: '缓存',
    subtotal: '小计', requests: '请求合计', activeDays: '有用量的天数', input: '输入 Token', output: '输出 Token', cache: '缓存 Token',
    total: '合计 TOTAL', note: '金额按各模型 API 公开价格估算 · 仅供参考', unpriced: n => `另有 ${compact(n)} tokens 的模型没有价格，未计入`,
    thanks: '* 感谢使用 OCTOPUS *', empty: '这段时间没有用量', title: 'Octopus 用量账单',
  },
  en: {
    subtitle: 'Local AI agents · usage bill', period: 'Period', issued: 'Issued', number: 'No.', currency: 'Currency', scope: 'Scope',
    allModels: 'All models', someModels: n => `${n} model${n === 1 ? '' : 's'}`, calls: 'req', inLabel: 'in', outLabel: 'out', cacheLabel: 'cache',
    subtotal: 'Subtotal', requests: 'Requests', activeDays: 'Active days', input: 'Input tokens', output: 'Output tokens', cache: 'Cache tokens',
    total: 'TOTAL', note: 'Amounts are estimated at public API prices · for reference only', unpriced: n => `${compact(n)} more tokens are of models without a price and not counted`,
    thanks: '* THANK YOU FOR USING OCTOPUS *', empty: 'No usage in this period', title: 'Octopus usage bill',
  },
};

/**
 * The receipt as one self-contained HTML page. `modelCount` and `allModels` tell the "scope" line what was chosen;
 * `issuedAt` is when it is made (a Date), `language` picks Chinese or English.
 */
export function renderHtml(bill, { language = 'en', modelCount = 0, allModels = true, issuedAt = new Date() } = {}) {
  const L = String(language).startsWith('zh') ? LABELS.zh : LABELS.en;
  const number = billNumber(bill);
  const stamp = `${issuedAt.getFullYear()}-${String(issuedAt.getMonth() + 1).padStart(2, '0')}-${String(issuedAt.getDate()).padStart(2, '0')} ${String(issuedAt.getHours()).padStart(2, '0')}:${String(issuedAt.getMinutes()).padStart(2, '0')}`;
  const period = bill.from === bill.to ? bill.from : `${bill.from} – ${bill.to}`;
  const t = bill.total;
  const info = (label, value) => `<div class="line"><span class="muted">${escapeHtml(label)}</span><span class="mono">${escapeHtml(value)}</span></div>`;
  const sums = (label, value) => `<div class="line"><span class="muted">${escapeHtml(label)}</span><span class="mono">${escapeHtml(value)}</span></div>`;
  const groups = bill.groups.map(g => `
    <section class="group">
      <div class="line head"><span><i class="sq"></i>${escapeHtml(g.agent.toUpperCase())}</span><span>${g.requests} ${L.calls}</span></div>
      ${g.models.map(m => `
      <div class="model">
        <div class="line"><span class="mono">${escapeHtml(m.model)}</span><b class="mono">${m.unpriced > 0 && m.cost === 0 ? '—' : money(m.cost)}</b></div>
        <div class="muted small mono">${m.requests} ${L.calls} · ${L.inLabel} ${compact(m.input)} / ${L.outLabel} ${compact(m.output)} / ${L.cacheLabel} ${compact(m.cache)}</div>
      </div>`).join('')}
      <div class="line sub"><b>${L.subtotal}</b><i class="leader"></i><b class="mono">${money(g.cost)}</b></div>
    </section>`).join('');
  const body = bill.groups.length > 0 ? groups : `<p class="muted center">${L.empty}</p>`;
  return `<!doctype html>
<html lang="${String(language).startsWith('zh') ? 'zh-CN' : 'en'}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(L.title)} ${escapeHtml(number)}</title>
<style>
  :root { color-scheme: light; }
  body { margin: 0; padding: 32px 12px; background: #e9e7e0; font: 14px/1.5 ui-monospace, "SF Mono", Menlo, Consolas, "Noto Sans Mono CJK SC", "PingFang SC", "Microsoft YaHei", monospace; color: #1a1a1a; }
  .receipt { box-sizing: border-box; width: 372px; max-width: 100%; margin: 0 auto; padding: 28px 26px 22px; background: #fbfaf6; box-shadow: 0 1px 4px rgba(0,0,0,.18); }
  .mono { font-variant-numeric: tabular-nums; }
  .muted { color: #6b6a66; }
  .small { font-size: 12px; }
  .center { text-align: center; }
  h1 { margin: 6px 0 2px; text-align: center; font-size: 26px; letter-spacing: .32em; font-weight: 800; }
  .tag { margin: 0 0 16px; text-align: center; font-size: 12px; color: #6b6a66; }
  .line { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; }
  hr { border: 0; border-top: 1px dashed #1a1a1a; margin: 14px 0; }
  hr.double { border-top: 3px double #1a1a1a; }
  .group { margin: 10px 0 16px; }
  .head { font-weight: 800; letter-spacing: .04em; }
  .sq { display: inline-block; width: 9px; height: 9px; margin-right: 8px; background: #1a1a1a; }
  .model { margin: 8px 0 0 14px; }
  .sub { margin: 8px 0 0 14px; }
  .leader { flex: 1; border-bottom: 1px dotted #6b6a66; transform: translateY(-4px); }
  .total { align-items: center; margin-top: 4px; }
  .total .label { font-weight: 800; letter-spacing: .06em; }
  .total .sum { font-size: 34px; font-weight: 800; line-height: 1.1; }
  .note { margin: 12px 0; text-align: center; font-size: 12px; color: #6b6a66; }
  .bars { display: block; width: 100%; height: 56px; margin-top: 16px; fill: #1a1a1a; }
  .no { margin: 8px 0 18px; text-align: center; letter-spacing: .3em; font-size: 12px; }
  .thanks { text-align: center; font-weight: 800; letter-spacing: .08em; font-size: 12px; }
  @media print { body { padding: 0; background: #fff; } .receipt { box-shadow: none; } }
</style>
</head>
<body>
<main class="receipt">
  <h1>OCTOPUS</h1>
  <p class="tag">${escapeHtml(L.subtitle)}</p>
  <hr>
  ${info(L.period, period)}
  ${info(L.issued, stamp)}
  ${info(L.number, number)}
  ${info(L.currency, 'USD')}
  ${info(L.scope, allModels ? L.allModels : L.someModels(modelCount))}
  <hr>
  ${body}
  <hr class="double">
  ${sums(L.requests, `${t.requests} ${L.calls}`)}
  ${sums(L.activeDays, String(bill.activeDays))}
  ${sums(L.input, t.input.toLocaleString('en-US'))}
  ${sums(L.output, t.output.toLocaleString('en-US'))}
  ${sums(L.cache, t.cache.toLocaleString('en-US'))}
  <hr>
  <div class="line total"><span class="label">${escapeHtml(L.total)}</span><span class="sum mono">${money(t.cost)}</span></div>
  <p class="note">${escapeHtml(L.note)}${t.unpriced > 0 ? `<br>${escapeHtml(L.unpriced(t.unpriced))}` : ''}</p>
  ${barcode(number)}
  <p class="no">${escapeHtml(number.replaceAll('-', ' '))}</p>
  <p class="thanks">${escapeHtml(L.thanks)}</p>
</main>
</body>
</html>
`;
}

const csvCell = value => {
  const s = String(value);
  return /[",\r\n]/.test(s) ? `"${s.replaceAll('"', '""')}"` : s;
};

/** One line per day, agent and model: the figures behind the receipt, for a spreadsheet. */
export function renderCsv(rows, { from, to, models } = {}) {
  const lines = ['date,agent,model,requests,input_tokens,output_tokens,cache_tokens,total_tokens,cost_usd,unpriced_tokens'];
  const picked = rows
    .filter(r => (!from || r.day >= from) && (!to || r.day <= to) && (!models || models.has(`${r.agent}|${r.model}`)) && r.input + r.output + r.cache > 0)
    .sort((a, b) => a.day.localeCompare(b.day) || a.agent.localeCompare(b.agent) || a.model.localeCompare(b.model));
  for (const r of picked) {
    lines.push([r.day, r.agent, r.model, r.requests, r.input, r.output, r.cache, r.input + r.output + r.cache, r.unpriced > 0 && r.cost === 0 ? '' : r.cost.toFixed(6), r.unpriced].map(csvCell).join(','));
  }
  return lines.join('\r\n') + '\r\n';
}
