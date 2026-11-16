// Model prices for the cost estimate of the usage history (design/plugins/12 §10), in USD per million tokens.
//
// The host syncs them from models.dev at most once a day and answers for the models we name (agents.modelPrices):
// the whole catalog is far more than a plugin turn can parse. Prices are cached here per sync; a new sync (another
// `syncedAt`) empties the cache, and the history recounts.
//
// A subscription is not billed per token: for one, the estimate is what the same work would cost through the API.

import { agents } from 'octopus';

/** model id → prices, or null when the host knows none. */
const cache = new Map();
/** { source: 'models.dev' | 'builtin', syncedAt?, count, syncing, error? } from the host's last answer. */
let info = { source: 'builtin', count: 0 };
let answered = false;

/**
 * Makes sure the prices of these models are known, asking the host for the missing ones. Returns true when the host's
 * prices changed since the last answer (a new sync): what was counted with the old ones should be counted again.
 */
export async function ensurePrices(models, { refresh = false, check = false } = {}) {
  const missing = [...new Set(models.filter(m => typeof m === 'string' && m.length > 0 && !cache.has(m)))];
  // `check`: ask even with nothing missing, to learn whether the host synced since.
  if (missing.length === 0 && !refresh && !check) return false;
  let answer;
  try {
    answer = await agents.modelPrices(missing, { refresh });
  } catch (e) {
    info = { ...info, error: e?.message ?? String(e) };
    for (const m of missing) cache.set(m, null);
    return false;
  }
  // The first sync after answers from the built-in table is a change too.
  const changed = answered && (answer.syncedAt ?? undefined) !== info.syncedAt;
  answered = true;
  info = { source: answer.source, syncedAt: answer.syncedAt ?? undefined, count: answer.count ?? 0, syncing: !!answer.syncing, error: answer.error ?? undefined };
  if (changed) cache.clear();
  for (const [model, price] of Object.entries(answer.prices ?? {})) cache.set(model, price ?? null);
  return changed;
}

/** Where the prices come from, for the history tab's footnote. */
export function priceSource() {
  return info;
}

/**
 * The estimated cost in USD of one reply, or undefined when its model has no price (or was not asked for). A reply
 * whose prompt (input and cache) passes the model's context tier is priced at the tier. The 1-hour cache write costs
 * twice the input price (Anthropic); the 5-minute one the listed cache write price.
 */
export function costOf(model, { input = 0, output = 0, cacheRead = 0, cacheWrite5m = 0, cacheWrite1h = 0 }) {
  const base = cache.get(model);
  if (!base) return undefined;
  const p = base.tier && input + cacheRead + cacheWrite5m + cacheWrite1h > base.tier.size ? base.tier : base;
  return (input * p.input + output * p.output + cacheRead * p.cacheRead + cacheWrite5m * p.cacheWrite + cacheWrite1h * p.input * 2) / 1e6;
}
