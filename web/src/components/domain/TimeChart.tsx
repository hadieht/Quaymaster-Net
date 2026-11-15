import { useEffect, useMemo, useState } from 'react';
import {
  Area,
  AreaChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ReferenceArea,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useTheme } from '@/state/theme';
import { dateAndTime, timeOnly } from '@/lib/format';

export interface ChartSeries {
  key: string;
  label: string;
  /** `accent` | `info` | `warning` | `healthy` | `critical` — theme tokens */
  color: 'accent' | 'info' | 'warning' | 'healthy' | 'critical';
}

function readColors() {
  const cs = getComputedStyle(document.documentElement);
  const get = (n: string) => `rgb(${cs.getPropertyValue(`--${n}`).trim().split(/\s+/).join(',')})`;
  return {
    accent: get('accent'),
    info: get('info'),
    warning: get('warning'),
    healthy: get('healthy'),
    critical: get('critical'),
    grid: get('border'),
    text: get('muted'),
    surface: get('surface'),
    fg: get('fg'),
  };
}

/** Theme-aware colours resolved from the CSS variables (SVG attributes cannot use var()). */
function useChartColors() {
  const { theme } = useTheme();
  const [c, setC] = useState(readColors);
  useEffect(() => setC(readColors()), [theme]);
  return c;
}

export interface TimeChartProps<T extends { t: number }> {
  data: T[];
  series: ChartSeries[];
  format: (v: number) => string;
  height?: number;
  ariaLabel: string;
  /** Fixed y-axis max (e.g. 100 for percentages). */
  yMax?: number;
  area?: boolean;
  stacked?: boolean;
  /** Visible x window (epoch ms). Defaults to the extent of `data`. Shared by every chart on a page to keep them in sync. */
  domain?: [number, number];
  /** Enables drag-to-zoom: called with the selected (ordered) epoch-ms range. */
  onSelect?: (from: number, to: number) => void;
}

/** One chart style for every time series in the app. */
export function TimeChart<T extends { t: number }>({
  data,
  series,
  format,
  height = 180,
  ariaLabel,
  yMax,
  area = true,
  domain,
  onSelect,
}: TimeChartProps<T>) {
  const colors = useChartColors();
  const tickStyle = useMemo(() => ({ fill: colors.text, fontSize: 11 }), [colors.text]);
  const [dragFrom, setDragFrom] = useState<number | null>(null);
  const [dragTo, setDragTo] = useState<number | null>(null);
  const spanMs = domain ? domain[1] - domain[0] : data.length > 1 ? data[data.length - 1]!.t - data[0]!.t : 0;
  const labelOf = spanMs > 24 * 3_600_000 ? dateAndTime : timeOnly;
  const endDrag = () => {
    if (onSelect && dragFrom !== null && dragTo !== null && dragFrom !== dragTo) {
      onSelect(Math.min(dragFrom, dragTo), Math.max(dragFrom, dragTo));
    }
    setDragFrom(null);
    setDragTo(null);
  };
  const common = {
    data,
    margin: { top: 6, right: 8, bottom: 0, left: 0 },
    ...(onSelect
      ? {
          onMouseDown: (e: { activeLabel?: string | number }) => {
            if (e?.activeLabel !== undefined) setDragFrom(Number(e.activeLabel));
          },
          onMouseMove: (e: { activeLabel?: string | number }) => {
            if (dragFrom !== null && e?.activeLabel !== undefined) setDragTo(Number(e.activeLabel));
          },
          onMouseUp: endDrag,
          onMouseLeave: () => {
            setDragFrom(null);
            setDragTo(null);
          },
        }
      : {}),
  };
  const axes = (
    <>
      <CartesianGrid stroke={colors.grid} strokeDasharray="3 3" vertical={false} />
      <XAxis
        dataKey="t"
        type="number"
        scale="time"
        domain={domain ?? ['dataMin', 'dataMax']}
        allowDataOverflow={Boolean(domain)}
        tickFormatter={(v: number) => labelOf(v)}
        tick={tickStyle}
        stroke={colors.grid}
        minTickGap={spanMs > 24 * 3_600_000 ? 72 : 48}
      />
      <YAxis
        tickFormatter={format}
        tick={tickStyle}
        stroke={colors.grid}
        width={56}
        domain={[0, yMax ?? 'auto']}
      />
      <Tooltip
        contentStyle={{
          background: colors.surface,
          border: `1px solid ${colors.grid}`,
          borderRadius: 8,
          fontSize: 12,
        }}
        labelStyle={{ color: colors.fg }}
        labelFormatter={(v) => (spanMs > 24 * 3_600_000 ? dateAndTime(Number(v)) : timeOnly(Number(v)))}
        formatter={(v, name) => [format(Number(v)), name]}
      />
      {dragFrom !== null && dragTo !== null ? (
        <ReferenceArea
          x1={dragFrom}
          x2={dragTo}
          fill={colors.accent}
          fillOpacity={0.15}
          stroke={colors.accent}
        />
      ) : null}
      {series.length > 1 ? <Legend wrapperStyle={{ fontSize: 12, color: colors.text }} /> : null}
    </>
  );
  return (
    <div role="img" aria-label={ariaLabel} style={{ height }} className="w-full">
      <ResponsiveContainer width="100%" height="100%">
        {area ? (
          <AreaChart {...common}>
            {axes}
            {series.map((s) => (
              <Area
                key={s.key}
                type="monotone"
                dataKey={s.key}
                name={s.label}
                stroke={colors[s.color]}
                fill={colors[s.color]}
                fillOpacity={0.15}
                strokeWidth={2}
                dot={false}
                isAnimationActive={false}
              />
            ))}
          </AreaChart>
        ) : (
          <LineChart {...common}>
            {axes}
            {series.map((s) => (
              <Line
                key={s.key}
                type="monotone"
                dataKey={s.key}
                name={s.label}
                stroke={colors[s.color]}
                strokeWidth={2}
                dot={false}
                isAnimationActive={false}
              />
            ))}
          </LineChart>
        )}
      </ResponsiveContainer>
    </div>
  );
}

export function ChartEmpty({
  children = 'Collecting data… the first points appear within a few seconds.',
}: {
  children?: string;
}) {
  return <div className="flex h-[180px] items-center justify-center text-sm text-muted">{children}</div>;
}
