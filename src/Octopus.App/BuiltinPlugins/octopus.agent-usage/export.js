// Exporting the usage bill (issue #5): a dialog to pick the period, the models and the format, then the system's save
// box. What the person picks in the box is the only place the plugin may write.

import { env, fs, ui } from 'octopus';
import { buildBill, isDay, money, renderCsv, renderHtml } from './bill.js';

const t = (zh, en) => ((env.language ?? '').startsWith('zh') ? zh : en);
const DAY = 24 * 60 * 60_000;

const pad = n => String(n).padStart(2, '0');
const dayOf = d => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const daysAgo = n => dayOf(new Date(Date.now() - n * DAY));

const PRESETS = [
  { id: '7', label: () => t('近 7 天', 'Last 7 days'), range: () => [daysAgo(6), daysAgo(0)] },
  { id: '30', label: () => t('近 30 天', 'Last 30 days'), range: () => [daysAgo(29), daysAgo(0)] },
  { id: 'month', label: () => t('本月', 'This month'), range: () => { const n = new Date(); return [dayOf(new Date(n.getFullYear(), n.getMonth(), 1)), dayOf(n)]; } },
  { id: 'last', label: () => t('上月', 'Last month'), range: () => { const n = new Date(); return [dayOf(new Date(n.getFullYear(), n.getMonth() - 1, 1)), dayOf(new Date(n.getFullYear(), n.getMonth(), 0))]; } },
];

/**
 * Opens the export dialog. `getRows` returns the history's rows (see bill.js) as they are now; they are read again on
 * every render, so a scan that finishes while the dialog is open shows up in it.
 */
export async function openBillExport(getRows) {
  const [from0, to0] = PRESETS[1].range();
  // `rev` goes up when the script sets a value itself (a preset, All, None): the host keeps what a field holds until then.
  const form = { from: from0, to: to0, format: 'html', picked: undefined, saving: false, rev: 0 };

  await ui.showDialog({
    title: t('导出账单', 'Export bill'),
    size: 'md',
    render: ({ close, invalidate }) => {
      const rows = getRows();
      // Every model seen, in the order of their cost, so the ones that matter come first.
      const known = new Map();
      for (const r of rows) {
        const key = `${r.agent}|${r.model}`;
        const k = known.get(key) ?? { key, agent: r.agent, model: r.model, tokens: 0, cost: 0 };
        k.tokens += r.input + r.output + r.cache;
        k.cost += r.cost;
        known.set(key, k);
      }
      const options = [...known.values()].filter(k => k.tokens > 0).sort((a, b) => b.cost - a.cost || b.tokens - a.tokens);
      // Until the person chooses, every model is in; models that appear later (a scan going on) are in too.
      const picked = form.picked ?? new Set(options.map(o => o.key));
      const chosen = options.filter(o => picked.has(o.key));
      const allModels = chosen.length === options.length;

      const rangeError = !isDay(form.from) || !isDay(form.to)
        ? t('日期格式是 YYYY-MM-DD', 'Dates look like YYYY-MM-DD')
        : form.from > form.to ? t('开始日期晚于结束日期', 'The start is after the end') : undefined;
      const bill = rangeError ? undefined : buildBill(rows, { from: form.from, to: form.to, models: new Set(chosen.map(o => o.key)) });
      const nothing = !bill || bill.total.requests === 0;

      const setPicked = next => { form.picked = next; form.rev++; invalidate(); };
      const toggle = (key, on) => {
        const next = new Set(picked);
        if (on) next.add(key); else next.delete(key);
        form.picked = next;
        invalidate();
      };

      const save = async () => {
        form.saving = true;
        invalidate();
        try {
          const range = form.from === form.to ? form.from : `${form.from}_${form.to}`;
          const target = await ui.pickSaveLocation({
            title: t('保存账单', 'Save bill'),
            defaultName: `octopus-usage-${range}.${form.format}`,
          });
          if (!target) return;
          const selection = new Set(chosen.map(o => o.key));
          const text = form.format === 'csv'
            ? renderCsv(rows, { from: form.from, to: form.to, models: selection })
            : renderHtml(bill, { language: env.language, modelCount: chosen.length, allModels });
          await fs.writeText(target, text);
          ui.toast(t(`账单已保存到 ${target}`, `Bill saved to ${target}`), { kind: 'success' });
          close(true);
        } catch (e) {
          ui.toast(t(`保存失败：${e?.message ?? e}`, `Could not save: ${e?.message ?? e}`), { kind: 'error' });
        } finally {
          form.saving = false;
          invalidate();
        }
      };

      return ui.scroll({}, ui.column({ gap: 3, padding: 2 }, [
        ui.column({ gap: 1 }, [
          ui.text(t('时间段', 'Period'), { variant: 'heading' }),
          ui.row({ gap: 2, wrap: true }, PRESETS.map(p => ui.button({
            label: p.label(), size: 'sm',
            onClick: () => { [form.from, form.to] = p.range(); form.rev++; invalidate(); },
          }))),
          ui.row({ gap: 2, align: 'start' }, [
            ui.column({ gap: 1, grow: 1 }, [
              ui.text(t('开始', 'From'), { variant: 'caption', color: 'muted' }),
              ui.datePicker({ key: 'from', value: form.from, rev: form.rev, onChange: v => { form.from = v; invalidate(); } }),
            ]),
            ui.column({ gap: 1, grow: 1 }, [
              ui.text(t('结束', 'To'), { variant: 'caption', color: 'muted' }),
              ui.datePicker({ key: 'to', value: form.to, rev: form.rev, onChange: v => { form.to = v; invalidate(); } }),
            ]),
          ]),
        ]),
        ui.column({ gap: 1 }, [
          ui.row({ justify: 'between', align: 'center' }, [
            ui.text(t(`模型（已选 ${chosen.length}/${options.length}）`, `Models (${chosen.length}/${options.length} selected)`), { variant: 'heading' }),
            ui.row({ gap: 1 }, [
              ui.button({ label: t('全选', 'All'), size: 'sm', variant: 'ghost', onClick: () => setPicked(new Set(options.map(o => o.key))) }),
              ui.button({ label: t('清空', 'None'), size: 'sm', variant: 'ghost', onClick: () => setPicked(new Set()) }),
            ]),
          ]),
          options.length === 0
            ? ui.text(t('还没有扫描到用量记录。', 'No usage records have been scanned yet.'), { variant: 'caption', color: 'muted' })
            : ui.column({ gap: 0 }, options.map(o => ui.checkbox({
              key: o.key, label: `${o.model}  ·  ${o.agent}`, value: picked.has(o.key), rev: form.rev, onChange: on => toggle(o.key, on),
            }))),
        ]),
        ui.column({ gap: 1 }, [
          ui.text(t('格式', 'Format'), { variant: 'heading' }),
          ui.segmented({
            value: form.format,
            options: [{ value: 'html', label: t('账单 HTML', 'Bill (HTML)') }, { value: 'csv', label: t('明细 CSV', 'Details (CSV)') }],
            onChange: v => { form.format = v; invalidate(); },
          }),
        ]),
        ui.text(
          rangeError ?? (nothing
            ? t('所选时间段和模型里没有用量。', 'No usage in the chosen period and models.')
            : t(`${bill.total.requests} 次请求 · 合计 ${money(bill.total.cost)}（估算）`, `${bill.total.requests} requests · total ${money(bill.total.cost)} (est.)`)),
          { variant: 'caption', color: rangeError || nothing ? 'danger' : 'muted' },
        ),
        ui.row({ justify: 'end', gap: 2 }, [
          ui.button({ label: t('取消', 'Cancel'), onClick: () => close(false) }),
          ui.button({ label: t('导出…', 'Export…'), variant: 'primary', icon: 'lucide:download', loading: form.saving, onClick: () => { if (!rangeError && !nothing && !form.saving) save(); } }),
        ]),
      ]));
    },
  });
}
