// Balance and quota of the model providers the user holds an API key for (design/plugins/12 §9.3, the "paste a key"
// path). The key is the user's own: given once in the popover, kept in Octopus's secret store (octopus.secrets) and
// sent only to that provider's own API host. Nothing here reads another application's files or credentials.
//
// Every provider turns its answer into the same snapshot as the agents (adapters.js), plus balances:
//   { plan?, windows: [{ id, label, usedPercent, resetsAt? }], balances?: [{ label, amount, currency, detail? }] }

import { env, net } from 'octopus';
import { windowLabel } from './adapters.js';

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);

/** A number, also from the strings some providers send ("110.00"). */
const num = v => {
  const n = typeof v === 'string' && v.trim() !== '' ? Number(v) : v;
  return typeof n === 'number' && Number.isFinite(n) ? n : undefined;
};

/** Unix seconds, Unix milliseconds or an ISO date → milliseconds. */
function when(value) {
  if (typeof value === 'number' && Number.isFinite(value) && value > 0) return value < 1e12 ? value * 1000 : value;
  if (typeof value === 'string') {
    const ms = /^\d+$/.test(value) ? when(Number(value)) : Date.parse(value);
    return Number.isFinite(ms) ? ms : undefined;
  }
  return undefined;
}

export class ProviderError extends Error {
  /** kind: 'auth' (the key was refused), 'http', 'format' (an answer we cannot read). */
  constructor(kind, message) {
    super(message);
    this.kind = kind;
  }
}

async function getJson(url, key, { bearer = true } = {}) {
  const response = await net.fetch(url, {
    headers: { Accept: 'application/json', Authorization: bearer ? `Bearer ${key}` : key },
  });
  let body;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }
  const said = body?.error?.message ?? body?.message ?? body?.msg ?? (typeof body?.error === 'string' ? body.error : undefined);
  if (response.status === 401 || response.status === 403) throw new ProviderError('auth', said ?? t('密钥无效或已失效', 'The key was refused'));
  if (!response.ok) throw new ProviderError('http', `HTTP ${response.status}${said ? ': ' + said : ''}`);
  if (body === undefined) throw new ProviderError('format', t('返回的不是 JSON', 'The answer is not JSON'));
  return body;
}

/** Tries each host in turn: a key belongs to one region, and only the other region refuses it. */
async function firstAccepted(urls, read) {
  let last;
  for (const url of urls) {
    try {
      return await read(url);
    } catch (e) {
      last = e;
      if (!(e instanceof ProviderError) || e.kind !== 'auth') throw e;
    }
  }
  throw last;
}

const money = (amount, currency) => {
  if (amount === undefined) return '—';
  const symbol = { CNY: '¥', USD: '$', RMB: '¥' }[String(currency ?? '').toUpperCase()];
  const fixed = Math.abs(amount) >= 1000 ? amount.toLocaleString(env.language, { maximumFractionDigits: 2 }) : amount.toFixed(2);
  return symbol ? `${symbol}${fixed}` : `${fixed}${currency ? ' ' + currency : ''}`;
};
export { money };

// ───────────────────────── the providers ─────────────────────────

export const PROVIDERS = [
  {
    id: 'deepseek',
    name: 'DeepSeek',
    hint: t('platform.deepseek.com → API keys', 'platform.deepseek.com → API keys'),
    console: 'https://platform.deepseek.com/usage',
    // https://api-docs.deepseek.com/api/get-user-balance
    async query(key) {
      const data = await getJson('https://api.deepseek.com/user/balance', key);
      const infos = Array.isArray(data?.balance_infos) ? data.balance_infos : [];
      if (infos.length === 0 && data?.is_available === undefined) throw new ProviderError('format', t('无法识别返回内容', 'Unrecognized answer'));
      return {
        windows: [],
        plan: data?.is_available === false ? t('余额不足', 'Out of balance') : undefined,
        balances: infos.map(b => ({
          label: t('余额', 'Balance'),
          amount: num(b.total_balance),
          currency: b.currency,
          detail: t(`赠送 ${money(num(b.granted_balance), b.currency)} · 充值 ${money(num(b.topped_up_balance), b.currency)}`,
            `granted ${money(num(b.granted_balance), b.currency)} · topped up ${money(num(b.topped_up_balance), b.currency)}`),
        })),
      };
    },
  },
  {
    id: 'moonshot',
    name: t('Kimi 开放平台', 'Kimi (Moonshot)'),
    hint: t('platform.moonshot.cn 或 platform.moonshot.ai 的 API Key', 'An API key of platform.moonshot.ai or platform.moonshot.cn'),
    console: 'https://platform.moonshot.cn/console/info',
    // https://platform.moonshot.cn/docs/api/balance — the China and the global platform have separate keys.
    async query(key) {
      const read = async url => {
        const body = await getJson(url, key);
        const data = body?.data;
        if (!data || num(data.available_balance) === undefined) throw new ProviderError('format', t('无法识别返回内容', 'Unrecognized answer'));
        const currency = url.includes('.cn/') ? 'CNY' : 'USD';
        return {
          windows: [],
          balances: [{
            label: t('可用余额', 'Available'),
            amount: num(data.available_balance),
            currency,
            detail: t(`现金 ${money(num(data.cash_balance), currency)} · 代金券 ${money(num(data.voucher_balance), currency)}`,
              `cash ${money(num(data.cash_balance), currency)} · vouchers ${money(num(data.voucher_balance), currency)}`),
          }],
        };
      };
      return firstAccepted(['https://api.moonshot.cn/v1/users/me/balance', 'https://api.moonshot.ai/v1/users/me/balance'], read);
    },
  },
  {
    id: 'openrouter',
    name: 'OpenRouter',
    hint: 'openrouter.ai/settings/keys',
    console: 'https://openrouter.ai/settings/credits',
    // https://openrouter.ai/docs/api-reference/get-current-api-key and …/get-credits
    async query(key) {
      const keyInfo = (await getJson('https://openrouter.ai/api/v1/key', key))?.data;
      if (!keyInfo || typeof keyInfo !== 'object') throw new ProviderError('format', t('无法识别返回内容', 'Unrecognized answer'));
      const windows = [];
      const limit = num(keyInfo.limit);
      if (limit > 0) {
        const used = num(keyInfo.limit_remaining) !== undefined ? limit - num(keyInfo.limit_remaining) : num(keyInfo.usage) ?? 0;
        const period = { daily: t('每天', 'daily'), weekly: t('每周', 'weekly'), monthly: t('每月', 'monthly') }[keyInfo.limit_reset];
        windows.push({ id: 'key-limit', label: period ? t(`密钥额度 · ${period}`, `Key limit · ${period}`) : t('密钥额度', 'Key limit'), usedPercent: Math.max(0, (used / limit) * 100) });
      }
      const balances = [];
      // The account's credits; a key may not be allowed to read them, which leaves only its own limit.
      try {
        const credits = (await getJson('https://openrouter.ai/api/v1/credits', key))?.data;
        const total = num(credits?.total_credits);
        const usage = num(credits?.total_usage);
        if (total !== undefined && usage !== undefined) {
          balances.push({
            label: t('剩余额度', 'Credits left'), amount: total - usage, currency: 'USD',
            detail: t(`已用 ${money(usage, 'USD')} / 共 ${money(total, 'USD')}`, `${money(usage, 'USD')} of ${money(total, 'USD')} used`),
          });
        }
      } catch { /* the key's limit alone */ }
      if (limit > 0) {
        balances.push({ label: t('密钥剩余', 'Key remaining'), amount: num(keyInfo.limit_remaining) ?? limit - (num(keyInfo.usage) ?? 0), currency: 'USD' });
      }
      return { windows, balances, plan: keyInfo.is_free_tier ? t('免费档', 'Free tier') : undefined };
    },
  },
  {
    id: 'siliconflow',
    name: t('硅基流动', 'SiliconFlow'),
    hint: t('cloud.siliconflow.cn → API 密钥', 'cloud.siliconflow.com → API keys'),
    console: 'https://cloud.siliconflow.cn/expensebill',
    // https://docs.siliconflow.cn/cn/api-reference/userinfo/get-user-info (the e-mail and name in the answer are not kept)
    async query(key) {
      const read = async url => {
        const data = (await getJson(url, key))?.data;
        if (!data || num(data.totalBalance) === undefined) throw new ProviderError('format', t('无法识别返回内容', 'Unrecognized answer'));
        const currency = url.includes('.cn/') ? 'CNY' : 'USD';
        return {
          windows: [],
          balances: [{
            label: t('余额', 'Balance'),
            amount: num(data.totalBalance),
            currency,
            detail: t(`充值 ${money(num(data.chargeBalance), currency)} · 赠送 ${money(num(data.balance), currency)}`,
              `charged ${money(num(data.chargeBalance), currency)} · granted ${money(num(data.balance), currency)}`),
          }],
        };
      };
      return firstAccepted(['https://api.siliconflow.cn/v1/user/info', 'https://api.siliconflow.com/v1/user/info'], read);
    },
  },
  {
    id: 'zhipu',
    name: t('智谱 GLM 编程套餐', 'GLM Coding Plan (Z.ai)'),
    hint: t('open.bigmodel.cn 或 z.ai 的 API Key（订阅了编程套餐的账号）', 'An API key of z.ai or open.bigmodel.cn (an account with the Coding Plan)'),
    console: 'https://bigmodel.cn/usercenter/glm-coding/usage',
    // Not a documented API: the endpoint the vendor's own usage page reads. It may change without notice.
    unofficial: true,
    async query(key) {
      const read = async url => {
        const body = await getJson(url, key, { bearer: false });
        const limits = body?.data?.limits;
        if (!Array.isArray(limits)) throw new ProviderError(body?.success === false ? 'auth' : 'format', body?.msg ?? t('无法识别返回内容', 'Unrecognized answer'));
        const windows = limits.map((l, i) => {
          const total = num(l.usage);
          const used = num(l.currentValue);
          const percent = num(l.percentage) ?? (total > 0 && used !== undefined ? (used / total) * 100 : undefined);
          if (percent === undefined) return undefined;
          // unit 3 = hours (the 5-hour prompt window); TIME_LIMIT counts the month's tool calls (search, reader…).
          const label = l.type === 'TOKENS_LIMIT'
            ? (num(l.unit) === 3 && num(l.number) ? windowLabel(num(l.number) * 60) : t('提示词额度', 'Prompts'))
            : l.type === 'TIME_LIMIT' ? t('工具调用 · 每月', 'Tool calls · monthly') : String(l.type ?? `#${i + 1}`);
          const minutes = l.type === 'TOKENS_LIMIT' && num(l.unit) === 3 && num(l.number) ? num(l.number) * 60 : undefined;
          return { id: `${l.type ?? 'limit'}-${i}`, label, usedPercent: percent, resetsAt: when(l.nextResetTime), durationMs: minutes ? minutes * 60_000 : undefined };
        }).filter(Boolean);
        return { windows, balances: [] };
      };
      return firstAccepted(['https://open.bigmodel.cn/api/monitor/usage/quota/limit', 'https://api.z.ai/api/monitor/usage/quota/limit'], read);
    },
  },
];

export const providerById = id => PROVIDERS.find(p => p.id === id);
