import { describe, expect, it } from 'vitest';
import {
  clampWindow,
  pan,
  rangeOptions,
  selectionToWindow,
  spanLabel,
  zoomIn,
  zoomOut,
  zoomSteps,
} from './chartWindow';

const H = 3_600_000;
const bounds = { from: 0, to: 72 * H };

describe('rangeOptions', () => {
  it('offers up to 7 d and hides options beyond retention', () => {
    expect(rangeOptions(168).map((o) => o.key)).toEqual(['live', '60', '360', '1440', '4320', '10080']);
    expect(rangeOptions(48).map((o) => o.key)).toEqual(['live', '60', '360', '1440', 'all']);
  });
  it('adds an All option for a custom retention', () => {
    const o = rangeOptions(250);
    expect(o[o.length - 1]).toEqual({ key: 'all', label: 'All (250 h)', minutes: 15000 });
  });
  it('falls back to 24 h when retention is unknown', () => {
    expect(rangeOptions(undefined).map((o) => o.key)).toEqual(['live', '60', '360', '1440']);
  });
});

describe('zoom', () => {
  it('lists steps shorter than the span plus the span itself', () => {
    expect(zoomSteps(72 * H)).toEqual([1, 3, 6, 12, 24, 72].map((h) => h * H));
    expect(zoomSteps(250 * H).pop()).toBe(250 * H);
  });
  it('zooms in around the centre', () => {
    const w = zoomIn({ from: 0, to: 72 * H }, bounds);
    expect(w.to - w.from).toBe(24 * H);
    expect((w.from + w.to) / 2).toBe(36 * H);
    expect(zoomIn({ from: 10 * H, to: 11 * H }, bounds)).toEqual({ from: 10 * H, to: 11 * H });
  });
  it('zooms out and returns null at the full span', () => {
    const w = zoomOut({ from: 30 * H, to: 36 * H }, bounds);
    expect(w && w.to - w.from).toBe(12 * H);
    expect(zoomOut({ from: 0, to: 24 * H }, bounds)).toBeNull();
  });
  it('clamps inside the bounds', () => {
    expect(clampWindow({ from: -5 * H, to: 1 * H }, 0, 72 * H)).toEqual({ from: 0, to: 6 * H });
    expect(clampWindow({ from: 70 * H, to: 76 * H }, 0, 72 * H)).toEqual({ from: 66 * H, to: 72 * H });
  });
  it('pans by half a window', () => {
    expect(pan({ from: 10 * H, to: 16 * H }, bounds, 1)).toEqual({ from: 13 * H, to: 19 * H });
    expect(pan({ from: 0, to: 6 * H }, bounds, -1)).toEqual({ from: 0, to: 6 * H });
  });
});

describe('selection and labels', () => {
  it('orders a drag selection and rejects tiny ones', () => {
    expect(selectionToWindow(5 * H, 2 * H, bounds)).toEqual({ from: 2 * H, to: 5 * H });
    expect(selectionToWindow(2 * H, 2 * H + 1000, bounds)).toBeNull();
  });
  it('labels spans', () => {
    expect(spanLabel(6 * H)).toBe('6 h');
    expect(spanLabel(72 * H)).toBe('3 d');
    expect(spanLabel(250 * H)).toBe('10 d 10 h');
  });
});
