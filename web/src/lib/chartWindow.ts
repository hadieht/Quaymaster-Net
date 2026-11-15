/** Pure helpers for the shared, hour-based visible window of the resource charts. */

export interface TimeWindow {
  /** Epoch ms. */
  from: number;
  to: number;
}

const HOUR = 3_600_000;
const MIN = 60_000;

/** Zoom steps in hours, ascending. The full retained window is always appended by `zoomSteps`. */
export const ZOOM_STEP_HOURS = [1, 3, 6, 12, 24, 72, 168] as const;

export interface RangeOption {
  key: string;
  label: string;
  /** Minutes of history. `null` = live. */
  minutes: number | null;
}

/** Live / 1 h / 6 h / 24 h / 3 d / 7 d / All, hiding every option longer than the retention. */
export function rangeOptions(retentionHours: number | undefined): RangeOption[] {
  const maxMin = retentionHours && retentionHours > 0 ? retentionHours * 60 : 24 * 60;
  const fixed: RangeOption[] = [
    { key: 'live', label: 'Live', minutes: null },
    { key: '60', label: '1 h', minutes: 60 },
    { key: '360', label: '6 h', minutes: 360 },
    { key: '1440', label: '24 h', minutes: 1440 },
    { key: '4320', label: '3 d', minutes: 4320 },
    { key: '10080', label: '7 d', minutes: 10080 },
  ];
  const out = fixed.filter((o) => o.minutes === null || o.minutes <= maxMin || o.minutes === 60);
  if (maxMin > 1440 && !out.some((o) => o.minutes === maxMin)) {
    out.push({ key: 'all', label: `All (${retentionHours} h)`, minutes: maxMin });
  }
  return out;
}

/** Selectable window lengths (ms) strictly shorter than or equal to the fetched span. */
export function zoomSteps(spanMs: number): number[] {
  const steps = ZOOM_STEP_HOURS.map((h) => h * HOUR).filter((ms) => ms < spanMs);
  return [...steps, spanMs];
}

/** Keep a window of the given length inside [min, max]. */
export function clampWindow(w: TimeWindow, min: number, max: number): TimeWindow {
  const len = Math.min(w.to - w.from, max - min);
  let from = w.from;
  if (from < min) from = min;
  if (from + len > max) from = max - len;
  return { from, to: from + len };
}

/** Next smaller step, centred on the current window. Returns the same window at the minimum (1 h). */
export function zoomIn(w: TimeWindow, bounds: TimeWindow): TimeWindow {
  const span = w.to - w.from;
  const steps = zoomSteps(bounds.to - bounds.from).filter((s) => s < span - MIN / 2);
  const next = steps[steps.length - 1];
  if (next === undefined) return w;
  const centre = (w.from + w.to) / 2;
  return clampWindow({ from: centre - next / 2, to: centre + next / 2 }, bounds.from, bounds.to);
}

/** Next larger step, centred on the current window. Returns `null` (= show everything) once it reaches the full span. */
export function zoomOut(w: TimeWindow, bounds: TimeWindow): TimeWindow | null {
  const full = bounds.to - bounds.from;
  const span = w.to - w.from;
  const next = zoomSteps(full).find((s) => s > span + MIN / 2);
  if (next === undefined || next >= full) return null;
  const centre = (w.from + w.to) / 2;
  return clampWindow({ from: centre - next / 2, to: centre + next / 2 }, bounds.from, bounds.to);
}

/** Pan by half a window; direction -1 = earlier, +1 = later. */
export function pan(w: TimeWindow, bounds: TimeWindow, direction: -1 | 1): TimeWindow {
  const delta = ((w.to - w.from) / 2) * direction;
  return clampWindow({ from: w.from + delta, to: w.to + delta }, bounds.from, bounds.to);
}

/** Turn a dragged selection into a window (min 1 minute wide, ordered, inside the bounds). Null if too small. */
export function selectionToWindow(a: number, b: number, bounds: TimeWindow): TimeWindow | null {
  const from = Math.max(bounds.from, Math.min(a, b));
  const to = Math.min(bounds.to, Math.max(a, b));
  return to - from >= 2 * MIN ? { from, to } : null;
}

/** Human label for a window length, e.g. "6 h", "2 d 4 h". */
export function spanLabel(ms: number): string {
  const h = Math.round(ms / HOUR);
  if (h < 1) return `${Math.max(1, Math.round(ms / MIN))} min`;
  if (h < 48) return `${h} h`;
  const d = Math.floor(h / 24);
  const r = h % 24;
  return r === 0 ? `${d} d` : `${d} d ${r} h`;
}
