// Each adapter turns one agent's raw data into a usage snapshot (design/plugins/12 §5):
//   { account?, plan?, windows: [{ id, label, usedPercent, resetsAt?, durationMs?, tag? }], credits?, session?, source, unavailable? }
// durationMs (the window's length) lets the popover tell how much of the window has passed and whether, at the current
// pace, it runs out before it resets; tag is a qualifier kept in the short label ("7d Opus").
// Only official sources: Claude's status line, Codex's app-server, Antigravity CLI's /usage, Copilot CLI's
// account.getQuota and Cursor's `about`.
// Nothing here reads another tool's credentials (design 12 §9 is still undecided).

import { env } from 'octopus';

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);

/** "5 hours", "Weekly", "30 days"… from a duration in minutes; no fixed list (Codex has a 43200-minute window here). */
export function windowLabel(minutes) {
  if (typeof minutes !== 'number' || !(minutes > 0)) return t('窗口', 'Window');
  if (Math.abs(minutes - 300) <= 1) return t('5 小时', '5 hours');
  if (Math.abs(minutes - 10080) <= 1) return t('每周', 'Weekly');
  if (minutes % 1440 === 0) return t(`${minutes / 1440} 天`, `${minutes / 1440} days`);
  if (minutes % 60 === 0) return t(`${minutes / 60} 小时`, `${minutes / 60} hours`);
  return t(`${minutes} 分钟`, `${minutes} minutes`);
}

const num = v => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);
const seconds = v => (num(v) !== undefined ? v * 1000 : undefined);

// ───────────────────────── Claude: status line payload ─────────────────────────

export function claudeFromStatusLine(payload, account) {
  if (!payload || typeof payload !== 'object') return undefined;
  const limits = payload.rate_limits;
  const windows = [];
  const add = (id, label, minutes, w, tag) => {
    if (num(w?.used_percentage) === undefined) return;
    windows.push({ id, label, usedPercent: w.used_percentage, resetsAt: seconds(w.resets_at), durationMs: minutes * 60_000, tag });
  };
  add('five_hour', t('5 小时', '5 hours'), 300, limits?.five_hour);
  add('seven_day', t('7 天', '7 days'), 10080, limits?.seven_day);
  add('seven_day_opus', t('7 天 · Opus', '7 days · Opus'), 10080, limits?.seven_day_opus, 'Opus');
  const context = payload.context_window;
  const contextPercent = num(context?.used_percentage)
    ?? (num(context?.total_input_tokens) !== undefined && num(context?.context_window_size)
      ? (context.total_input_tokens / context.context_window_size) * 100 : undefined);
  const snapshot = {
    account,
    windows,
    session: {
      costUsd: num(payload.cost?.total_cost_usd),
      contextPercent,
      model: payload.model?.display_name ?? payload.model?.id,
    },
    source: 'official',
  };
  // An API-key session has no rate_limits at all: say so instead of showing 0%.
  if (!limits) snapshot.unavailable = { reason: 'loginHasNoQuota', hint: t('这个会话的登录方式没有订阅窗口（例如 API 密钥）。', 'This session signs in without subscription windows (an API key, for example).') };
  return snapshot;
}

// ───────────────────────── Codex: account/rateLimits/read ─────────────────────────

export function codexFromRateLimits(data, account) {
  // The probe selects rateLimits and rateLimitsByLimitId; prefer the "codex" bucket when there are several.
  const bucket = data?.rateLimitsByLimitId?.codex ?? data?.rateLimits;
  const windows = [bucket?.primary, bucket?.secondary]
    .filter(w => num(w?.usedPercent) !== undefined)
    .map(w => ({
      id: `m${w.windowDurationMins ?? 'unknown'}`,
      label: windowLabel(w.windowDurationMins),
      usedPercent: w.usedPercent,
      resetsAt: seconds(w.resetsAt),
      durationMs: num(w.windowDurationMins) ? w.windowDurationMins * 60_000 : undefined,
    }));
  const credits = bucket?.credits?.hasCredits
    ? { balance: bucket.credits.balance ?? undefined, unlimited: !!bucket.credits.unlimited }
    : undefined;
  const snapshot = { account, plan: bucket?.planType ?? undefined, windows, credits, source: 'official' };
  if (windows.length === 0) {
    snapshot.unavailable = { reason: 'loginHasNoQuota', hint: t('当前登录方式没有订阅额度（API 密钥或自定义服务商）。', 'The current sign-in has no subscription quota (an API key or a custom provider).') };
  }
  return snapshot;
}

// ───────────────────────── Antigravity CLI: `agy --print /usage --output-format json` ─────────────────────────

function agyWindowMinutes(windowText) {
  if (typeof windowText !== 'string') return undefined;
  const s = windowText.trim().toLowerCase();
  if (s === '5h') return 300;
  if (s === 'weekly' || s === '7d') return 10080;
  if (s === 'daily' || s === '1d') return 1440;
  let m = /^(\d+)h$/.exec(s);
  if (m) return Number(m[1]) * 60;
  m = /^(\d+)d$/.exec(s);
  if (m) return Number(m[1]) * 1440;
  m = /^(\d+)m$/.exec(s);
  if (m) return Number(m[1]);
  return undefined;
}

function agyGroupTag(name) {
  if (typeof name !== 'string' || name.trim().length === 0) return undefined;
  if (/gemini/i.test(name)) return 'Gemini';
  if (/claude/i.test(name) && /gpt/i.test(name)) return 'Claude/GPT';
  return name.replace(/\s+models?$/i, '').trim() || undefined;
}

export function agyFromUsage(data) {
  const groups = Array.isArray(data?.command?.data?.groups) ? data.command.data.groups : [];
  const multiple = groups.length > 1;
  const windows = [];
  for (const group of groups) {
    const tag = multiple ? agyGroupTag(group?.name) : undefined;
    const buckets = (Array.isArray(group?.buckets) ? group.buckets : [])
      .map((b, i) => ({ b, i, minutes: agyWindowMinutes(b?.window) }))
      .sort((a, b) => (a.minutes ?? Infinity) - (b.minutes ?? Infinity) || a.i - b.i);
    for (const { b, minutes } of buckets) {
      const remaining = num(b?.remaining_fraction);
      if (remaining === undefined) continue;
      const usedPercent = Math.max(0, Math.min(100, (1 - remaining) * 100));
      const base = minutes ? windowLabel(minutes) : (typeof b.name === 'string' && b.name.length > 0 ? b.name : t('窗口', 'Window'));
      windows.push({
        id: String(b.id ?? `${tag ?? 'agy'}-${b.window ?? windows.length}`),
        label: tag ? `${base} · ${tag}` : base,
        usedPercent,
        resetsAt: when(b.reset_time),
        durationMs: minutes ? minutes * 60_000 : undefined,
        tag,
      });
    }
  }
  const snapshot = { windows, source: 'official' };
  if (windows.length === 0) {
    snapshot.unavailable = {
      reason: 'loginHasNoQuota',
      hint: t('当前登录方式没有订阅额度（未登录或使用 API 密钥）。', 'The current sign-in has no subscription quota (not signed in or using an API key).'),
    };
  }
  return snapshot;
}

// ───────────────────────── GitHub Copilot CLI: `account.getQuota` ─────────────────────────

const COPILOT_ORDER = ['premium_interactions', 'chat', 'completions'];
const copilotLabel = key => ({
  premium_interactions: t('高级请求', 'Premium'),
  chat: t('对话', 'Chat'),
  completions: t('补全', 'Completions'),
}[key] ?? key.replaceAll('_', ' '));

export function copilotFromQuota(data, account) {
  const raw = data?.quotaSnapshots;
  const entries = Array.isArray(raw)
    ? raw.map((snap, i) => [String(snap?.type ?? snap?.quotaId ?? `q${i}`), snap])
    : raw && typeof raw === 'object' ? Object.entries(raw) : [];
  entries.sort(([a], [b]) => {
    const ia = COPILOT_ORDER.indexOf(a);
    const ib = COPILOT_ORDER.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
  });
  const windows = [];
  let hasUnlimited = false;
  for (const [key, snap] of entries) {
    if (!snap || typeof snap !== 'object') continue;
    if (snap.isUnlimitedEntitlement === true || snap.unlimited === true) {
      hasUnlimited = true;
      continue;
    }
    const remaining = num(snap.remainingPercentage ?? snap.remaining_percentage);
    const entitlement = num(snap.entitlementRequests ?? snap.entitlement_requests ?? snap.entitlement);
    const used = num(snap.usedRequests ?? snap.used_requests);
    let usedPercent;
    if (remaining !== undefined) usedPercent = Math.max(0, Math.min(100, 100 - remaining));
    else if (entitlement > 0 && used !== undefined) usedPercent = Math.max(0, (used / entitlement) * 100);
    if (usedPercent === undefined) continue;
    windows.push({
      id: key,
      label: copilotLabel(key),
      usedPercent,
      resetsAt: when(snap.resetDate ?? snap.reset_date),
    });
  }
  const snapshot = { account, windows, source: 'official' };
  if (windows.length === 0 && hasUnlimited) {
    snapshot.credits = { unlimited: true };
  } else if (windows.length === 0) {
    snapshot.unavailable = {
      reason: 'loginHasNoQuota',
      hint: t('当前登录方式没有可显示的 Copilot 额度；也可以在 GitHub 网页查看。', 'No Copilot quota is available for this sign-in; you can also view it on GitHub.'),
      action: { label: t('打开设置', 'Open settings'), url: 'https://github.com/settings/copilot/features' },
    };
  }
  return snapshot;
}

export function copilotUnavailable(account) {
  return {
    account,
    windows: [],
    source: 'official',
    retry: true,
    unavailable: {
      reason: 'noOfficialSource',
      hint: t('当前 Copilot CLI 未返回额度数据；可在 GitHub 网页查看。', 'Copilot CLI did not return quota data; view it on GitHub.'),
      action: { label: t('打开设置', 'Open settings'), url: 'https://github.com/settings/copilot/features' },
    },
  };
}

// ───────────────────────── Grok: no official quota source ─────────────────────────

export function grokSnapshot() {
  return {
    windows: [],
    source: 'official',
    unavailable: {
      reason: 'noOfficialSource',
      hint: t('Grok 没有提供可读取的额度接口；在 Grok 里输入 /usage 可以查看。', 'Grok offers no readable quota; type /usage in Grok to see it.'),
    },
  };
}

// ───────────────────────── Cursor: plan only ─────────────────────────

export function cursorFromAbout(data) {
  return {
    plan: data?.subscriptionTier ?? undefined,
    windows: [],
    source: 'official',
    unavailable: {
      reason: 'noOfficialSource',
      hint: t('Cursor 的用量只在网页控制台提供。', 'Cursor shows usage only in its web dashboard.'),
      action: { label: t('打开控制台', 'Open dashboard'), url: 'https://cursor.com/dashboard?tab=usage' },
    },
  };
}

// ───────────────────────── OpenCode: no quota of its own ─────────────────────────

export function opencodeSnapshot() {
  return {
    windows: [],
    source: 'official',
    unavailable: {
      reason: 'noOfficialSource',
      hint: t('OpenCode 不转述服务商的额度；Claude、Codex 账户的额度在各自的标签里，API 服务商的余额在“API 余额”里。', 'OpenCode does not pass on provider quotas; Claude and Codex accounts have their own tabs, API providers are under “API balance”.'),
    },
  };
}

/** `claude auth status --json` → "Max 20x", "Pro"…; undefined for an API key or a third-party provider. */
export function claudePlan(data) {
  if (!data || data.loggedIn === false) return undefined;
  if (data.apiProvider && data.apiProvider !== 'firstParty') return undefined;
  const raw = data.planTier ?? data.subscriptionType;
  if (typeof raw !== 'string' || raw.length === 0) return undefined;
  return raw.split('_').map(p => (/^\d+x$/i.test(p) ? p.toLowerCase() : p[0].toUpperCase() + p.slice(1))).join(' ');
}

// ───────────────────────── a user's own command ─────────────────────────

/** Unix seconds, Unix milliseconds or an ISO date → milliseconds. */
function when(value) {
  if (typeof value === 'number' && Number.isFinite(value)) return value < 1e12 ? value * 1000 : value;
  if (typeof value === 'string') {
    const ms = Date.parse(value);
    return Number.isFinite(ms) ? ms : undefined;
  }
  return undefined;
}

/**
 * What a custom command printed. Accepted: Claude's status line JSON (rate_limits…), Codex's rate limit result
 * (rateLimits…), or Octopus's own shape: { plan?, windows: [{ label?, usedPercent, resetsAt?, windowMinutes? }], session? }.
 */
export function fromCustom(data) {
  if (!data || typeof data !== 'object') return undefined;
  if (data.rate_limits) {
    const snapshot = claudeFromStatusLine(data);
    return snapshot && { ...snapshot, unavailable: undefined, source: 'custom' };
  }
  if (data.rateLimits || data.rateLimitsByLimitId) return { ...codexFromRateLimits(data), source: 'custom' };
  const windows = (Array.isArray(data.windows) ? data.windows : [])
    .map((w, i) => {
      const used = num(w?.usedPercent ?? w?.used_percentage ?? w?.percent);
      if (used === undefined) return undefined;
      const minutes = num(w.windowMinutes ?? w.windowDurationMins);
      return {
        id: String(w.id ?? `w${i}`), label: typeof w.label === 'string' ? w.label : windowLabel(minutes), usedPercent: used,
        resetsAt: when(w.resetsAt ?? w.resets_at), durationMs: minutes ? minutes * 60_000 : undefined,
      };
    })
    .filter(Boolean);
  const session = data.session && typeof data.session === 'object'
    ? { costUsd: num(data.session.costUsd ?? data.session.cost_usd), tokens: num(data.session.tokens), contextPercent: num(data.session.contextPercent) }
    : undefined;
  return {
    plan: typeof data.plan === 'string' ? data.plan : undefined,
    windows,
    session,
    source: 'custom',
    unavailable: windows.length > 0 ? undefined : { reason: 'failed', hint: t('命令的输出里没有用量窗口。', 'The command printed no usage windows.') },
  };
}

/** The tightest window of a snapshot, or undefined. */
export function tightest(snapshot) {
  let best;
  for (const w of snapshot?.windows ?? []) if (!best || w.usedPercent > best.usedPercent) best = w;
  return best;
}
