/**
 * @fileoverview Timeline under the map: a histogram of activities, visits and
 * health data, with a legend; drag to select a range, wheel or pinch to zoom,
 * horizontal scroll, shift-wheel, shift-drag or two fingers to move along time
 * @module Contacts/atlas/AtlasTimeline
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Box, Button, IconButton, Tooltip, Typography } from '@mui/material';
import { ChevronLeft, ChevronRight, ZoomIn, ZoomOut } from '@mui/icons-material';
import type { AtlasQuery, HistogramRow } from './types';
import { kindColor } from './types';
import AtlasLegend from './AtlasLegend';
import { MAX_SPAN_MS, MIN_SPAN_MS, bucketFor, bucketMs, panRange, zoomRange } from './geo';
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
  /** The map's area, types and text: the bars count what the map shows */
  filter?: AtlasQuery;
  /** Time of the item selected on the map or in the list */
  highlight?: Range | null;
}

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
  filter,
  highlight,
}) => {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(800);
  const [rows, setRows] = useState<HistogramRow[]>([]);
  const [drag, setDrag] = useState<{ kind: 'select' | 'pan'; x0: number; view0: Range } | null>(
    null,
  );
  const [draft, setDraft] = useState<Range | null>(null);
  const bucket = bucketFor(view.to - view.from);
  // Latest view and callback for the native wheel listener (registered once)
  const viewRef = useRef(view);
  viewRef.current = view;
  const changeRef = useRef(onViewChange);
  changeRef.current = onViewChange;
  // Touch points on the strip (x per pointer) and an ongoing pinch
  const pointers = useRef(new Map<number, number>());
  const pinch = useRef<{ d0: number; t0: number; view0: Range } | null>(null);

  // A native, non-passive listener: React's onWheel cannot stop the page (or
  // the panel around the timeline) from scrolling at the same time
  useEffect(() => {
    const el = ref.current;
    if (!el) {
      return undefined;
    }
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const v = viewRef.current;
      const rect = el.getBoundingClientRect();
      const unit = e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? rect.width : 1;
      const dx = (e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX) * unit;
      const dy = e.shiftKey ? 0 : e.deltaY * unit;
      if (Math.abs(dx) > Math.abs(dy)) {
        // Horizontal scroll (touchpad, shift + wheel): move along time
        changeRef.current(panRange(v, dx / rect.width));
        return;
      }
      const center = v.from + ((e.clientX - rect.left) / rect.width) * (v.to - v.from);
      // Smooth on touchpads (small deltas), about 20% per mouse-wheel notch
      const factor = Math.min(2, Math.max(0.5, Math.exp(dy * 0.002)));
      changeRef.current(zoomRange(v, center, factor));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

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
          filter,
        )
        .then((r) => alive && setRows(r))
        .catch(() => alive && setRows([]));
    }, 200);
    return () => {
      alive = false;
      clearTimeout(t);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view.from, view.to, bucket, reloadKey, JSON.stringify(filter ?? {})]);

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

  const px = (e: React.PointerEvent) => e.clientX - ref.current!.getBoundingClientRect().left;

  const endPointer = (e: React.PointerEvent) => {
    pointers.current.delete(e.pointerId);
    if (pinch.current) {
      // A pinch never ends in a selection
      if (pointers.current.size < 2) {
        pinch.current = null;
      }
      return;
    }
    if (drag?.kind === 'select' && draft && x(draft.to) - x(draft.from) > 3) {
      onSelectionChange(draft);
    }
    setDraft(null);
    setDrag(null);
  };

  // Kinds in view, for the legend
  const legend = useMemo(() => rows.map((r) => r.kind), [rows]);

  const center = (view.from + view.to) / 2;

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
            : 'Drag to choose a time range · wheel or pinch to zoom · scroll sideways or shift-drag to move'}
        </Typography>
        <Box sx={{ flex: 1 }} />
        <Tooltip title="Earlier">
          <IconButton
            size="small"
            aria-label="Earlier"
            onClick={() => onViewChange(panRange(view, -0.5))}
          >
            <ChevronLeft fontSize="small" />
          </IconButton>
        </Tooltip>
        <Tooltip title="Zoom out">
          <span>
            <IconButton
              size="small"
              aria-label="Zoom out"
              disabled={view.to - view.from >= MAX_SPAN_MS}
              onClick={() => onViewChange(zoomRange(view, center, 2))}
            >
              <ZoomOut fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
        <Tooltip title="Zoom in">
          <span>
            <IconButton
              size="small"
              aria-label="Zoom in"
              disabled={view.to - view.from <= MIN_SPAN_MS}
              onClick={() => onViewChange(zoomRange(view, center, 0.5))}
            >
              <ZoomIn fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
        <Tooltip title="Later">
          <IconButton
            size="small"
            aria-label="Later"
            onClick={() => onViewChange(panRange(view, 0.5))}
          >
            <ChevronRight fontSize="small" />
          </IconButton>
        </Tooltip>
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
        onPointerDown={(e) => {
          (e.target as Element).setPointerCapture?.(e.pointerId);
          pointers.current.set(e.pointerId, px(e));
          if (pointers.current.size === 2) {
            // Second finger: pinch to zoom and move, instead of selecting
            const [a, b] = [...pointers.current.values()];
            pinch.current = {
              d0: Math.max(10, Math.abs(a - b)),
              t0: tAt((a + b) / 2),
              view0: view,
            };
            setDraft(null);
            setDrag(null);
            return;
          }
          if (pointers.current.size === 1) {
            setDrag({ kind: e.shiftKey ? 'pan' : 'select', x0: px(e), view0: view });
          }
        }}
        onPointerMove={(e) => {
          if (pointers.current.has(e.pointerId)) {
            pointers.current.set(e.pointerId, px(e));
          }
          if (pinch.current && pointers.current.size >= 2) {
            const [a, b] = [...pointers.current.values()];
            const { d0, t0, view0 } = pinch.current;
            const span = Math.min(
              Math.max(((view0.to - view0.from) * d0) / Math.max(10, Math.abs(a - b)), MIN_SPAN_MS),
              MAX_SPAN_MS,
            );
            // The moment first under the fingers stays under them
            const from = t0 - ((a + b) / 2 / width) * span;
            onViewChange({ from, to: from + span });
            return;
          }
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
        onPointerUp={endPointer}
        onPointerCancel={endPointer}
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
              fill={kindColor(b.kind)}
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
          {highlight && (
            // The selected activity or visit: a white marker over its time
            <rect
              x={x(highlight.from) - 1}
              y={0}
              width={Math.max(3, x(highlight.to) - x(highlight.from) + 2)}
              height={HEIGHT - AXIS}
              fill="#ffffff"
              opacity={0.35}
              stroke="#ffffff"
              strokeWidth={1}
            >
              <title>Selected</title>
            </rect>
          )}
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
      <Box sx={{ pb: 0.75 }}>
        <AtlasLegend kinds={legend} healthAsStrip />
      </Box>
    </Box>
  );
};

export default AtlasTimeline;
