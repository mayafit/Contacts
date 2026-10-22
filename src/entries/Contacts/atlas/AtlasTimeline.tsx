/**
 * @fileoverview Timeline under the map: a histogram of activities, visits and
 * health data; drag to select a range, scroll to zoom, shift-drag to pan
 * @module Contacts/atlas/AtlasTimeline
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Box, Button, Typography } from '@mui/material';
import type { HistogramRow } from './types';
import { typeColor } from './types';
import { bucketFor, bucketMs } from './geo';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { tokens } from '../theme/contactsTheme';

/** A time range */
export interface Range {
  from: number;
  to: number;
}

/** Props for AtlasTimeline */
export interface AtlasTimelineProps {
  /** Visible part of the timeline */
  view: Range;
  onViewChange: (r: Range) => void;
  /** Selected range (filters the map), null for none */
  selection: Range | null;
  onSelectionChange: (r: Range | null) => void;
  /** Bumped after imports to reload the counts */
  reloadKey: number;
}

const KIND_COLORS: Record<string, string> = { visit: '#ffb547', health: '#ff4d6d' };

const HEIGHT = 120;
/** Height of the health-data band at the top */
const HEALTH_BAND = 6;
const AXIS = 18;

/**
 * Timeline strip.
 * @param props - Component props
 * @returns Timeline element
 */
const AtlasTimeline: React.FC<AtlasTimelineProps> = ({
  view,
  onViewChange,
  selection,
  onSelectionChange,
  reloadKey,
}) => {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(800);
  const [rows, setRows] = useState<HistogramRow[]>([]);
  const [drag, setDrag] = useState<{ kind: 'select' | 'pan'; x0: number; view0: Range } | null>(
    null,
  );
  const [draft, setDraft] = useState<Range | null>(null);
  const bucket = bucketFor(view.to - view.from);

  useEffect(() => {
    const el = ref.current;
    if (!el) {
      return undefined;
    }
    const ro = new ResizeObserver(() => setWidth(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    let alive = true;
    const t = setTimeout(() => {
      backendApiClient
        .atlasHistogram(
          new Date(view.from).toISOString(),
          new Date(view.to).toISOString(),
          bucket,
          Intl.DateTimeFormat().resolvedOptions().timeZone,
        )
        .then((r) => alive && setRows(r))
        .catch(() => alive && setRows([]));
    }, 200);
    return () => {
      alive = false;
      clearTimeout(t);
    };
  }, [view.from, view.to, bucket, reloadKey]);

  const x = (t: number) => ((t - view.from) / (view.to - view.from)) * width;
  const tAt = (px: number) => view.from + (px / width) * (view.to - view.from);

  // Stack per bucket: activity types first, then visits, then health (scaled down: it is dense)
  const bars = useMemo(() => {
    const byBucket = new Map<number, HistogramRow[]>();
    rows.forEach((r) => {
      const t = Date.parse(r.bucket);
      byBucket.set(t, [...(byBucket.get(t) ?? []), r]);
    });
    // Activities and visits are stacked bars; health data (dense: a value every
    // few seconds) is a thin band along the top, so it does not drown the rest
    const max = Math.max(
      1,
      ...[...byBucket.values()].map((list) =>
        list.filter((r) => r.kind !== 'health').reduce((s, r) => s + r.count, 0),
      ),
    );
    const out: { t: number; kind: string; y: number; h: number; count: number }[] = [];
    byBucket.forEach((list, t) => {
      let y = 0;
      list
        .filter((r) => r.kind !== 'health')
        .forEach((r) => {
          const h = (r.count / max) * (HEIGHT - AXIS - HEALTH_BAND - 6);
          out.push({ t, kind: r.kind, y, h, count: r.count });
          y += h;
        });
      const health = list.find((r) => r.kind === 'health');
      if (health) {
        out.push({ t, kind: 'health', y: -1, h: HEALTH_BAND, count: health.count });
      }
    });
    return out;
  }, [rows]);

  const ticks = useMemo(() => {
    const span = view.to - view.from;
    const step =
      span > 4 * 365 * 86_400_000
        ? 'year'
        : span > 120 * 86_400_000
          ? 'month'
          : span > 4 * 86_400_000
            ? 'day'
            : 'hour';
    const out: { t: number; label: string }[] = [];
    const d = new Date(view.from);
    if (step === 'year') {
      d.setMonth(0, 1);
    }
    if (step === 'month' || step === 'year') {
      d.setDate(1);
    }
    if (step !== 'hour') {
      d.setHours(0, 0, 0, 0);
    } else {
      d.setMinutes(0, 0, 0);
    }
    while (d.getTime() <= view.to && out.length < 400) {
      const t = d.getTime();
      if (t >= view.from) {
        out.push({
          t,
          label:
            step === 'year'
              ? String(d.getFullYear())
              : step === 'month'
                ? d.toLocaleDateString('en-GB', { month: 'short', year: '2-digit' })
                : step === 'day'
                  ? d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
                  : `${d.getHours()}:00`,
        });
      }
      if (step === 'year') {
        d.setFullYear(d.getFullYear() + 1);
      } else if (step === 'month') {
        d.setMonth(d.getMonth() + 1);
      } else if (step === 'day') {
        d.setDate(d.getDate() + 1);
      } else {
        d.setHours(d.getHours() + 1);
      }
    }
    const every = Math.max(1, Math.ceil(out.length / Math.max(1, width / 70)));
    return out.filter((_, i) => i % every === 0);
  }, [view, width]);

  const onWheel = (e: React.WheelEvent) => {
    const rect = ref.current!.getBoundingClientRect();
    const center = tAt(e.clientX - rect.left);
    const factor = e.deltaY > 0 ? 1.25 : 0.8;
    const span = Math.min(
      Math.max((view.to - view.from) * factor, 3_600_000 * 6),
      40 * 365 * 86_400_000,
    );
    const ratio = (center - view.from) / (view.to - view.from);
    onViewChange({ from: center - span * ratio, to: center + span * (1 - ratio) });
  };

  const px = (e: React.PointerEvent) => e.clientX - ref.current!.getBoundingClientRect().left;

  const sel = draft ?? selection;
  const barW = Math.max(1, (bucketMs(bucket) / (view.to - view.from)) * width - 1);

  return (
    <Box
      sx={{ borderTop: `1px solid ${tokens.border}`, bgcolor: tokens.surface, px: 1.5, pt: 0.5 }}
    >
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, height: 26 }}>
        <Typography variant="caption" sx={{ color: tokens.textMuted }}>
          {sel
            ? `${new Date(sel.from).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })} – ${new Date(sel.to).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })}`
            : 'Drag on the timeline to choose a time range · scroll to zoom · shift-drag to move'}
        </Typography>
        <Box sx={{ flex: 1 }} />
        {selection && (
          <Button size="small" onClick={() => onSelectionChange(null)}>
            Clear time
          </Button>
        )}
        {selection && (
          <Button
            size="small"
            onClick={() => {
              const pad = (selection.to - selection.from) * 0.1;
              onViewChange({ from: selection.from - pad, to: selection.to + pad });
            }}
          >
            Zoom to selection
          </Button>
        )}
      </Box>
      <Box
        ref={ref}
        onWheel={onWheel}
        onPointerDown={(e) => {
          (e.target as Element).setPointerCapture?.(e.pointerId);
          setDrag({ kind: e.shiftKey ? 'pan' : 'select', x0: px(e), view0: view });
        }}
        onPointerMove={(e) => {
          if (!drag) {
            return;
          }
          if (drag.kind === 'pan') {
            const dt = ((px(e) - drag.x0) / width) * (drag.view0.to - drag.view0.from);
            onViewChange({ from: drag.view0.from - dt, to: drag.view0.to - dt });
          } else {
            const a = tAt(drag.x0);
            const b = tAt(px(e));
            setDraft({ from: Math.min(a, b), to: Math.max(a, b) });
          }
        }}
        onPointerUp={() => {
          if (drag?.kind === 'select') {
            if (draft && x(draft.to) - x(draft.from) > 3) {
              onSelectionChange(draft);
            }
          }
          setDraft(null);
          setDrag(null);
        }}
        sx={{
          position: 'relative',
          height: HEIGHT,
          cursor: drag?.kind === 'pan' ? 'grabbing' : 'crosshair',
          userSelect: 'none',
          touchAction: 'none',
        }}
        role="slider"
        aria-label="Timeline"
      >
        <svg width={width} height={HEIGHT}>
          {bars.map((b, i) => (
            <rect
              key={i}
              x={x(b.t)}
              y={b.kind === 'health' ? 0 : HEIGHT - AXIS - b.y - b.h}
              width={barW}
              height={b.h}
              fill={KIND_COLORS[b.kind] ?? typeColor(b.kind)}
              opacity={b.kind === 'health' ? 0.7 : 0.9}
            >
              <title>{`${b.kind}: ${b.count}`}</title>
            </rect>
          ))}
          <line x1={0} x2={width} y1={HEIGHT - AXIS} y2={HEIGHT - AXIS} stroke={tokens.border} />
          {ticks.map((t) => (
            <g key={t.t}>
              <line
                x1={x(t.t)}
                x2={x(t.t)}
                y1={HEIGHT - AXIS}
                y2={HEIGHT - AXIS + 4}
                stroke={tokens.textMuted}
              />
              <text x={x(t.t) + 3} y={HEIGHT - 4} fontSize={10} fill={tokens.textMuted}>
                {t.label}
              </text>
            </g>
          ))}
          {sel && (
            <rect
              x={x(sel.from)}
              y={0}
              width={Math.max(1, x(sel.to) - x(sel.from))}
              height={HEIGHT - AXIS}
              fill="#3987e5"
              opacity={0.18}
              stroke="#3987e5"
            />
          )}
        </svg>
      </Box>
    </Box>
  );
};

export default AtlasTimeline;
