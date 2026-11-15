import { Button } from '@/components/ui';
import { pan, spanLabel, zoomIn, zoomOut, type TimeWindow } from '@/lib/chartWindow';

/**
 * Zoom in/out, Earlier/Later and Reset for a chart window. `win` is the zoomed window (null = whole range),
 * `bounds` the fetched span of the un-zoomed range; panning and zooming stay inside it.
 */
export function ChartZoomControls({
  win,
  bounds,
  onChange,
}: {
  win: TimeWindow | null;
  bounds: TimeWindow;
  onChange: (w: TimeWindow | null) => void;
}) {
  const visible = win ?? bounds;
  const zoomedIn = win !== null;
  return (
    <div role="group" aria-label="Zoom" className="flex flex-wrap items-center gap-1.5">
      <Button
        size="sm"
        aria-label="Zoom in"
        onClick={() => onChange(zoomIn(visible, bounds))}
        disabledReason={visible.to - visible.from <= 3_600_000 ? 'Already at the 1 h minimum' : null}
      >
        Zoom in
      </Button>
      <Button
        size="sm"
        aria-label="Zoom out"
        onClick={() => onChange(zoomOut(visible, bounds))}
        disabledReason={zoomedIn ? null : 'Showing the whole range'}
      >
        Zoom out
      </Button>
      <Button
        size="sm"
        aria-label="Earlier"
        onClick={() => win && onChange(pan(win, bounds, -1))}
        disabledReason={zoomedIn ? null : 'Zoom in first'}
      >
        ◀
      </Button>
      <Button
        size="sm"
        aria-label="Later"
        onClick={() => win && onChange(pan(win, bounds, 1))}
        disabledReason={zoomedIn ? null : 'Zoom in first'}
      >
        ▶
      </Button>
      <Button
        size="sm"
        variant="ghost"
        aria-label="Reset zoom"
        onClick={() => onChange(null)}
        disabledReason={zoomedIn ? null : 'Showing the whole range'}
      >
        Reset
      </Button>
      <span className="text-xs text-muted" aria-live="polite">
        Showing {spanLabel(visible.to - visible.from)} · drag on a chart to zoom
      </span>
    </div>
  );
}
