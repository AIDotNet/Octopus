// Each adapter turns one agent's raw data into a usage snapshot (design/plugins/12 §5):
//   { account?, plan?, windows: [{ id, label, usedPercent, resetsAt? }], credits?, session?, source, unavailable? }
// Only official sources: Claude's status line, Codex's app-server, Cursor's `about`, OpenCode's plugin events.
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
  const add = (id, label, w) => {
    if (num(w?.used_percentage) === undefined) return;
    windows.push({ id, label, usedPercent: w.used_percentage, resetsAt: seconds(w.resets_at) });
  };
  add('five_hour', t('5 小时', '5 hours'), limits?.five_hour);
  add('seven_day', t('7 天', '7 days'), limits?.seven_day);
  add('seven_day_opus', t('7 天 · Opus', '7 days · Opus'), limits?.seven_day_opus);
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

// ───────────────────────── Grok: no official quota source ─────────────────────────

export function grokSnapshot(statusLine) {
  return {
    windows: [],
    session: statusLine ? { costUsd: num(statusLine.cost?.total_cost_usd ?? statusLine.cost_usd), contextPercent: num(statusLine.context_window?.used_percentage) } : undefined,
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

// ───────────────────────── OpenCode: session.usage.updated ─────────────────────────

/** Sessions add up: one OpenCode window can run several sessions, each reporting its own running totals. */
export function opencodeFromSessions(sessions) {
  let costUsd = 0;
  let tokens = 0;
  for (const s of sessions.values()) {
    costUsd += s.cost ?? 0;
    tokens += s.tokens ?? 0;
  }
  return {
    windows: [],
    session: { costUsd, tokens },
    source: 'official',
    unavailable: {
      reason: 'noOfficialSource',
      hint: t('OpenCode 不转述服务商的额度；若用的是 Claude 或 Codex 账户，额度在对应的一栏。', 'OpenCode does not pass on provider quotas; a Claude or Codex account shows its quota in its own row.'),
    },
  };
}

/** The event's payload: { properties: { sessionID, cost, tokens: { input, output, reasoning, cache: { read, write } } } }. */
export function opencodeUsageEvent(payload) {
  const p = payload?.properties ?? payload ?? {};
  const id = p.sessionID ?? p.sessionId ?? p.info?.id;
  if (!id) return undefined;
  const tk = p.tokens ?? p.info?.tokens ?? {};
  const tokens = typeof tk === 'number' ? tk
    : (num(tk.input) ?? 0) + (num(tk.output) ?? 0) + (num(tk.reasoning) ?? 0) + (num(tk.cache?.read) ?? 0) + (num(tk.cache?.write) ?? 0);
  return { id, cost: num(p.cost ?? p.info?.cost), tokens };
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
      return { id: String(w.id ?? `w${i}`), label: typeof w.label === 'string' ? w.label : windowLabel(minutes), usedPercent: used, resetsAt: when(w.resetsAt ?? w.resets_at) };
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
