/**
 * @fileoverview The Settings page (FR-68 to FR-71, AD-41): text sizes, the pointer, map
 * symbols, colours, route styles and chart colours, each with its default, a reset and a
 * preview; changes apply at once and are saved by themselves
 * @module Contacts/settings/SettingsPage
 */

import React, { useId } from 'react';
import { useNavigate } from 'react-router';
import {
  Box,
  Button,
  Link,
  MenuItem,
  Paper,
  Slider,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import { tokens } from '../theme/contactsTheme';
import { useSettings } from './SettingsProvider';
import type { SaveState } from './SettingsProvider';
import { cursorValue } from './pointer';
import {
  DEFAULT_UI,
  PATTERNS,
  POINTER_SIZES,
  RANGES,
  STYLED_TYPES,
  dashArray,
  fs,
} from './uiSettings';
import type { LinePattern, PointerSize, StyledType, UiSettings } from './uiSettings';

/** The sections, in order */
export const SECTIONS = [
  { id: 'text', title: 'Text' },
  { id: 'pointer', title: 'Pointer' },
  { id: 'map', title: 'Map symbols' },
  { id: 'colors', title: 'Colours' },
  { id: 'routes', title: 'Routes' },
  { id: 'charts', title: 'Charts' },
  { id: 'other', title: 'Other settings' },
] as const;

const TYPE_LABELS: Record<StyledType, string> = {
  ride: 'Ride',
  hike: 'Hike',
  run: 'Run',
  walk: 'Walk',
  drive: 'Drive',
  transit: 'Transit',
  flight: 'Flight',
  ski: 'Ski',
  swim: 'Swim',
  other: 'Other',
};

/** A copy with one value changed at a path */
const setAt = (ui: UiSettings, path: (string | number)[], value: unknown): UiSettings => {
  const copy = structuredClone(ui) as unknown as Record<string, unknown>;
  let at = copy as Record<string | number, unknown>;
  path.slice(0, -1).forEach((k) => {
    at = at[k] as Record<string | number, unknown>;
  });
  at[path[path.length - 1]] = value;
  return copy as unknown as UiSettings;
};

/**
 * When the settings were saved, or that saving failed.
 * @param s - Where saving stands
 * @returns The line
 */
export const saveText = (s: SaveState): string => {
  switch (s.state) {
    case 'saving':
      return 'Saving…';
    case 'saved':
      return `Saved ${s.at.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}`;
    case 'failed':
      return `Not saved: ${s.message}. Your changes are kept in this browser and saved with the next change.`;
    default:
      return 'Changes apply at once and are saved by themselves.';
  }
};

const Section: React.FC<{
  id: string;
  title: string;
  onReset?: () => void;
  children: React.ReactNode;
}> = ({ id, title, onReset, children }) => (
  <Paper
    component="section"
    id={`settings-${id}`}
    aria-labelledby={`settings-${id}-title`}
    variant="outlined"
    sx={{ p: 2, mb: 2, scrollMarginTop: 16 }}
  >
    <Box sx={{ display: 'flex', alignItems: 'center', mb: 1.5 }}>
      <Typography id={`settings-${id}-title`} variant="h3" component="h2" sx={{ flex: 1 }}>
        {title}
      </Typography>
      {onReset && (
        <Button size="small" onClick={onReset}>
          Reset
        </Button>
      )}
    </Box>
    {children}
  </Paper>
);

/** A number with a slider, its value and its default */
const NumberSetting: React.FC<{
  label: string;
  value: number;
  def: number;
  range: string;
  unit?: string;
  onChange: (v: number) => void;
}> = ({ label, value, def, range, unit = 'px', onChange }) => {
  const [min, max, step] = RANGES[range];
  const id = useId();
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', sm: '200px 1fr 120px' },
        alignItems: 'center',
        gap: { sm: 2 },
        py: 0.5,
      }}
    >
      <Typography id={id} variant="body2">
        {label}
      </Typography>
      <Slider
        aria-labelledby={id}
        getAriaValueText={(v) => `${v}${unit}`}
        value={value}
        min={min}
        max={max}
        step={step}
        onChange={(_, v) => onChange(v as number)}
        valueLabelDisplay="auto"
        size="small"
      />
      <Typography variant="caption" sx={{ color: value === def ? tokens.textMuted : tokens.text }}>
        {value}
        {unit} {value === def ? '(default)' : `(default ${def}${unit})`}
      </Typography>
    </Box>
  );
};

/** A colour with its default */
const ColorSetting: React.FC<{
  label: string;
  value: string;
  def: string;
  onChange: (v: string) => void;
}> = ({ label, value, def, onChange }) => (
  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.25 }}>
    <Box
      component="input"
      type="color"
      value={value}
      aria-label={`${label} colour`}
      onChange={(e: React.ChangeEvent<HTMLInputElement>) => onChange(e.target.value)}
      sx={{
        width: 36,
        height: 28,
        p: 0,
        border: `1px solid ${tokens.borderStrong}`,
        borderRadius: 1,
        bgcolor: 'transparent',
      }}
    />
    <Typography variant="body2" sx={{ flex: 1 }}>
      {label}
    </Typography>
    {value !== def && (
      <Button
        size="small"
        onClick={() => onChange(def)}
        aria-label={`default: reset the ${label} colour`}
      >
        default
      </Button>
    )}
  </Box>
);

/** A line pattern */
const PatternSetting: React.FC<{
  label: string;
  value: LinePattern;
  def: LinePattern;
  onChange: (v: LinePattern) => void;
}> = ({ label, value, def, onChange }) => (
  <TextField
    select
    size="small"
    label={label}
    value={value}
    helperText={value === def ? 'default' : `default ${def}`}
    onChange={(e) => onChange(e.target.value as LinePattern)}
    sx={{ minWidth: 140 }}
  >
    {PATTERNS.map((p) => (
      <MenuItem key={p} value={p}>
        {p}
      </MenuItem>
    ))}
  </TextField>
);

/** The map symbols on a dark map, at their sizes, with the grab distance as a faint ring */
const MapPreview: React.FC<{ ui: UiSettings }> = ({ ui }) => {
  const m = ui.map;
  const c = ui.colors;
  const ring = (cx: number, cy: number, r: number) =>
    m.grab > 0 ? (
      <circle
        cx={cx}
        cy={cy}
        r={r + m.grab}
        fill="none"
        stroke="#ffffff"
        strokeOpacity={0.25}
        strokeDasharray="2 3"
      />
    ) : null;
  return (
    <Box
      component="svg"
      role="img"
      aria-label="Map symbols preview"
      viewBox="0 0 520 120"
      sx={{ width: '100%', maxWidth: 640, bgcolor: '#0e1116', borderRadius: 1, display: 'block' }}
    >
      {ring(40, 60, m.visit)}
      <circle cx={40} cy={60} r={m.visit} fill={c.visit} fillOpacity={0.85} stroke={c.visit} />
      <circle
        cx={100}
        cy={60}
        r={m.visitSelected}
        fill={c.visit}
        fillOpacity={0.85}
        stroke="#ffffff"
        strokeWidth={2}
      />
      {ring(160, 60, m.photo)}
      <circle cx={160} cy={60} r={m.photo} fill={c.photo} stroke="#0e1116" />
      <polyline
        points="200,80 230,50 260,70 290,40"
        fill="none"
        stroke={c.ride}
        strokeWidth={ui.routes.types.ride.width}
      />
      {[
        [200, 80],
        [230, 50],
        [260, 70],
        [290, 40],
      ].map(([x, y]) => (
        <circle key={`${x}`} cx={x} cy={y} r={m.trackPoint} fill={c.highlightDark} />
      ))}
      <circle cx={330} cy={60} r={m.hover} fill={c.health} stroke="#ffffff" strokeWidth={2} />
      <circle cx={380} cy={60} r={m.airport} fill={c.flight} stroke="#0e1116" />
      <circle
        cx={440}
        cy={60}
        r={m.waypoint / 2}
        fill={c.waypoint}
        stroke="#ffffff"
        strokeWidth={2}
      />
      <text
        x={440}
        y={60}
        textAnchor="middle"
        dominantBaseline="central"
        fill="#ffffff"
        style={{ fontSize: fs.map(11), fontWeight: 600 }}
      >
        1
      </text>
      <rect
        x={480}
        y={60 - m.photoPin / 4}
        width={m.photoPin / 2}
        height={m.photoPin / 2}
        rx={3}
        fill="#3a4250"
        stroke={c.photo}
        strokeWidth={2}
      />
    </Box>
  );
};

/** Every route type's line, and a matched route's pieces */
const RoutesPreview: React.FC<{ ui: UiSettings }> = ({ ui }) => {
  const r = ui.routes;
  return (
    <Box
      component="svg"
      role="img"
      aria-label="Routes preview"
      viewBox={`0 0 520 ${STYLED_TYPES.length * 18 + 70}`}
      sx={{ width: '100%', maxWidth: 640, bgcolor: '#0e1116', borderRadius: 1, display: 'block' }}
    >
      {STYLED_TYPES.map((t, i) => (
        <g key={t}>
          <text x={8} y={18 * i + 18} fill="#a3acb9" style={{ fontSize: fs.chart(11) }}>
            {TYPE_LABELS[t]}
          </text>
          <line
            x1={80}
            x2={500}
            y1={18 * i + 14}
            y2={18 * i + 14}
            stroke={ui.colors[t]}
            strokeWidth={r.types[t].width}
            strokeOpacity={r.types[t].opacity}
            strokeDasharray={dashArray(r.types[t].pattern, r.types[t].width)}
            strokeLinecap="round"
          />
        </g>
      ))}
      <g transform={`translate(0 ${STYLED_TYPES.length * 18 + 20})`}>
        <text x={8} y={20} fill="#a3acb9" style={{ fontSize: fs.chart(11) }}>
          Pieces
        </text>
        <line
          x1={80}
          x2={220}
          y1={16}
          y2={16}
          stroke={ui.colors.drive}
          strokeWidth={r.observedWidth}
          strokeOpacity={0.95}
        />
        <line
          x1={220}
          x2={360}
          y1={16}
          y2={16}
          stroke="#1f2937"
          strokeWidth={r.inferred.width + 2.5}
          strokeOpacity={0.75}
        />
        <line
          x1={220}
          x2={360}
          y1={16}
          y2={16}
          stroke={ui.colors.inferred}
          strokeWidth={r.inferred.width}
          strokeDasharray={dashArray(r.inferred.pattern, r.inferred.width)}
        />
        <line
          x1={360}
          x2={500}
          y1={16}
          y2={16}
          stroke={ui.colors.gap}
          strokeWidth={r.gap.width}
          strokeOpacity={0.55}
          strokeDasharray={dashArray(r.gap.pattern, r.gap.width)}
        />
        <text x={150} y={40} textAnchor="middle" fill="#6f7a89" style={{ fontSize: fs.chart(10) }}>
          observed
        </text>
        <text x={290} y={40} textAnchor="middle" fill="#6f7a89" style={{ fontSize: fs.chart(10) }}>
          inferred
        </text>
        <text x={430} y={40} textAnchor="middle" fill="#6f7a89" style={{ fontSize: fs.chart(10) }}>
          gap
        </text>
      </g>
    </Box>
  );
};

/**
 * The Settings page.
 * @returns Page element
 */
const SettingsPage: React.FC = () => {
  const { ui, update, resetAll, save } = useSettings();
  const navigate = useNavigate();
  const d = DEFAULT_UI;
  const set = (path: (string | number)[]) => (v: unknown) => update((u) => setAt(u, path, v));
  const reset =
    <K extends keyof UiSettings>(key: K) =>
    () =>
      update((u) => ({ ...u, [key]: structuredClone(d[key]) }));

  return (
    <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto', bgcolor: tokens.page }}>
      <Box sx={{ maxWidth: 980, mx: 'auto', p: { xs: 1.5, sm: 3 } }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 1, flexWrap: 'wrap' }}>
          <Typography variant="h1" sx={{ flex: 1 }}>
            Settings
          </Typography>
          <Button variant="outlined" color="warning" onClick={resetAll}>
            Reset everything
          </Button>
        </Box>
        <Typography
          variant="body2"
          role={save.state === 'failed' ? 'alert' : undefined}
          sx={{ color: save.state === 'failed' ? tokens.attention : tokens.textSecondary, mb: 1.5 }}
        >
          {saveText(save)}
        </Typography>
        <Box
          component="nav"
          aria-label="Settings sections"
          sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 2 }}
        >
          {SECTIONS.map((s) => (
            <Button key={s.id} size="small" variant="text" href={`#settings-${s.id}`}>
              {s.title}
            </Button>
          ))}
        </Box>

        <Section id="text" title="Text" onReset={reset('text')}>
          <NumberSetting
            label="Body text"
            value={ui.text.body}
            def={d.text.body}
            range="text.body"
            onChange={set(['text', 'body'])}
          />
          <NumberSetting
            label="Small text"
            value={ui.text.small}
            def={d.text.small}
            range="text.small"
            onChange={set(['text', 'small'])}
          />
          <NumberSetting
            label="Headings"
            value={ui.text.headings}
            def={d.text.headings}
            range="text.headings"
            unit="%"
            onChange={set(['text', 'headings'])}
          />
          <NumberSetting
            label="Charts"
            value={ui.text.charts}
            def={d.text.charts}
            range="text.charts"
            onChange={set(['text', 'charts'])}
          />
          <NumberSetting
            label="Map labels"
            value={ui.text.map}
            def={d.text.map}
            range="text.map"
            onChange={set(['text', 'map'])}
          />
          <NumberSetting
            label="Navigation"
            value={ui.text.navigation}
            def={d.text.navigation}
            range="text.navigation"
            onChange={set(['text', 'navigation'])}
          />
          <Box
            aria-label="Text preview"
            sx={{ mt: 1.5, p: 1.5, borderRadius: 1, bgcolor: tokens.surface }}
          >
            <Typography variant="h2">A heading</Typography>
            <Typography variant="body1">
              Body text: lists, panels and buttons read like this.
            </Typography>
            <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary }}>
              Small text: captions, labels and notes.
            </Typography>
            <Box component="span" sx={{ fontSize: fs.chart(11), color: tokens.textMuted, mr: 2 }}>
              Chart axis 0 5 10 km
            </Box>
            <Box component="span" sx={{ fontSize: fs.map(11), color: tokens.textMuted, mr: 2 }}>
              Map label
            </Box>
            <Box
              component="span"
              sx={{ fontSize: fs.nav(11.5), fontWeight: 600, color: tokens.textSecondary }}
            >
              Navigation
            </Box>
          </Box>
        </Section>

        <Section id="pointer" title="Pointer" onReset={reset('pointer')}>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
            The system&apos;s pointer, or Echo&apos;s own at a larger size: the arrow, the hand over
            things you can click, the hand that grabs the map, the crosshair for marking and
            drawing, and the text cursor.
          </Typography>
          <ToggleButtonGroup
            exclusive
            size="small"
            value={ui.pointer.size}
            onChange={(_, v: PointerSize | null) => v !== null && set(['pointer', 'size'])(v)}
            aria-label="Pointer size"
          >
            {POINTER_SIZES.map((p) => (
              <ToggleButton key={String(p)} value={p}>
                {p === 'system' ? 'System' : `${p} px`}
              </ToggleButton>
            ))}
          </ToggleButtonGroup>
          <Typography variant="caption" sx={{ color: tokens.textMuted, ml: 1 }}>
            {ui.pointer.size === 'system' ? '(default)' : '(default: System)'}
          </Typography>
          {ui.pointer.size !== 'system' && (
            <Box
              aria-label="Pointer preview"
              sx={{
                display: 'flex',
                gap: 2,
                mt: 1.5,
                p: 1.5,
                borderRadius: 1,
                bgcolor: '#3a4250',
                alignItems: 'center',
              }}
            >
              {(['arrow', 'hand', 'grab', 'grabbing', 'crosshair', 'text'] as const).map(
                (shape) => (
                  <Box
                    key={shape}
                    component="img"
                    alt={shape}
                    src={cursorValue(shape, ui.pointer.size as number, 'auto').replace(
                      /^url\("(.*)"\).*$/,
                      '$1',
                    )}
                    sx={{ width: ui.pointer.size, height: ui.pointer.size }}
                  />
                ),
              )}
            </Box>
          )}
        </Section>

        <Section id="map" title="Map symbols" onReset={reset('map')}>
          <NumberSetting
            label="Visits"
            value={ui.map.visit}
            def={d.map.visit}
            range="map.visit"
            onChange={set(['map', 'visit'])}
          />
          <NumberSetting
            label="Selected visit"
            value={ui.map.visitSelected}
            def={d.map.visitSelected}
            range="map.visitSelected"
            onChange={set(['map', 'visitSelected'])}
          />
          <NumberSetting
            label="Photo points"
            value={ui.map.photo}
            def={d.map.photo}
            range="map.photo"
            onChange={set(['map', 'photo'])}
          />
          <NumberSetting
            label="Photo pins"
            value={ui.map.photoPin}
            def={d.map.photoPin}
            range="map.photoPin"
            onChange={set(['map', 'photoPin'])}
          />
          <NumberSetting
            label="Recorded points"
            value={ui.map.trackPoint}
            def={d.map.trackPoint}
            range="map.trackPoint"
            onChange={set(['map', 'trackPoint'])}
          />
          <NumberSetting
            label="Hover marker"
            value={ui.map.hover}
            def={d.map.hover}
            range="map.hover"
            onChange={set(['map', 'hover'])}
          />
          <NumberSetting
            label="Airports"
            value={ui.map.airport}
            def={d.map.airport}
            range="map.airport"
            onChange={set(['map', 'airport'])}
          />
          <NumberSetting
            label="Marked points"
            value={ui.map.waypoint}
            def={d.map.waypoint}
            range="map.waypoint"
            onChange={set(['map', 'waypoint'])}
          />
          <NumberSetting
            label="Grab distance"
            value={ui.map.grab}
            def={d.map.grab}
            range="map.grab"
            onChange={set(['map', 'grab'])}
          />
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mb: 1 }}>
            The grab distance is how far beyond a point or a line a click or hover still takes it,
            without drawing it larger (the dotted ring in the preview).
          </Typography>
          <MapPreview ui={ui} />
        </Section>

        <Section id="colors" title="Colours" onReset={reset('colors')}>
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
              columnGap: 3,
            }}
          >
            {STYLED_TYPES.map((t) => (
              <ColorSetting
                key={t}
                label={TYPE_LABELS[t]}
                value={ui.colors[t]}
                def={d.colors[t]}
                onChange={set(['colors', t])}
              />
            ))}
            {(
              [
                ['visit', 'Visits'],
                ['photo', 'Photos'],
                ['health', 'Heart rate and health'],
                ['area', 'Area filter'],
                ['inferred', 'Inferred route pieces'],
                ['gap', 'Gaps'],
                ['waypoint', 'Marked points'],
                ['highlightDark', 'Selected route (dark maps)'],
                ['highlightLight', 'Selected route (light maps)'],
              ] as const
            ).map(([k, label]) => (
              <ColorSetting
                key={k}
                label={label}
                value={ui.colors[k]}
                def={d.colors[k]}
                onChange={set(['colors', k])}
              />
            ))}
          </Box>
        </Section>

        <Section id="routes" title="Routes" onReset={reset('routes')}>
          {STYLED_TYPES.map((t) => (
            <Box
              key={t}
              sx={{
                display: 'flex',
                flexWrap: 'wrap',
                alignItems: 'center',
                gap: 2,
                py: 0.5,
                borderBottom: `1px solid ${tokens.border}`,
              }}
            >
              <Typography variant="body2" sx={{ width: 80, fontWeight: 600 }}>
                {TYPE_LABELS[t]}
              </Typography>
              <Box sx={{ flex: 1, minWidth: 220 }}>
                <NumberSetting
                  label={`${TYPE_LABELS[t]} width`}
                  value={ui.routes.types[t].width}
                  def={d.routes.types[t].width}
                  range="routes.width"
                  onChange={set(['routes', 'types', t, 'width'])}
                />
                <NumberSetting
                  label={`${TYPE_LABELS[t]} opacity`}
                  value={ui.routes.types[t].opacity}
                  def={d.routes.types[t].opacity}
                  range="routes.opacity"
                  unit=""
                  onChange={set(['routes', 'types', t, 'opacity'])}
                />
              </Box>
              <PatternSetting
                label={`${TYPE_LABELS[t]} pattern`}
                value={ui.routes.types[t].pattern}
                def={d.routes.types[t].pattern}
                onChange={set(['routes', 'types', t, 'pattern'])}
              />
            </Box>
          ))}
          <Box sx={{ mt: 1.5 }}>
            <NumberSetting
              label="Selected route width"
              value={ui.routes.selectedWidth}
              def={d.routes.selectedWidth}
              range="routes.selectedWidth"
              onChange={set(['routes', 'selectedWidth'])}
            />
            <NumberSetting
              label="Other routes while one is selected"
              value={ui.routes.dimmedOpacity}
              def={d.routes.dimmedOpacity}
              range="routes.dimmedOpacity"
              unit=""
              onChange={set(['routes', 'dimmedOpacity'])}
            />
            <NumberSetting
              label="Width with many routes"
              value={ui.routes.crowdedWidth}
              def={d.routes.crowdedWidth}
              range="routes.crowdedWidth"
              onChange={set(['routes', 'crowdedWidth'])}
            />
            <NumberSetting
              label="Observed pieces"
              value={ui.routes.observedWidth}
              def={d.routes.observedWidth}
              range="routes.observedWidth"
              onChange={set(['routes', 'observedWidth'])}
            />
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'center' }}>
              <Box sx={{ flex: 1, minWidth: 260 }}>
                <NumberSetting
                  label="Inferred pieces"
                  value={ui.routes.inferred.width}
                  def={d.routes.inferred.width}
                  range="routes.inferred.width"
                  onChange={set(['routes', 'inferred', 'width'])}
                />
              </Box>
              <PatternSetting
                label="Inferred pattern"
                value={ui.routes.inferred.pattern}
                def={d.routes.inferred.pattern}
                onChange={set(['routes', 'inferred', 'pattern'])}
              />
            </Box>
            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'center' }}>
              <Box sx={{ flex: 1, minWidth: 260 }}>
                <NumberSetting
                  label="Gaps"
                  value={ui.routes.gap.width}
                  def={d.routes.gap.width}
                  range="routes.gap.width"
                  onChange={set(['routes', 'gap', 'width'])}
                />
              </Box>
              <PatternSetting
                label="Gap pattern"
                value={ui.routes.gap.pattern}
                def={d.routes.gap.pattern}
                onChange={set(['routes', 'gap', 'pattern'])}
              />
            </Box>
          </Box>
          <Box sx={{ mt: 1.5 }}>
            <RoutesPreview ui={ui} />
          </Box>
        </Section>

        <Section id="charts" title="Charts" onReset={reset('charts')}>
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
              columnGap: 3,
            }}
          >
            {(
              [
                ['speed', 'Speed'],
                ['elevation', 'Elevation'],
                ['cadence', 'Cadence'],
              ] as const
            ).map(([k, label]) => (
              <ColorSetting
                key={k}
                label={label}
                value={ui.charts[k]}
                def={d.charts[k]}
                onChange={set(['charts', k])}
              />
            ))}
            {ui.charts.zones.map((z, i) => (
              <ColorSetting
                key={i}
                label={`Heart-rate zone ${i + 1}`}
                value={z}
                def={d.charts.zones[i]}
                onChange={set(['charts', 'zones', i])}
              />
            ))}
          </Box>
          <Box
            component="svg"
            role="img"
            aria-label="Chart lines preview"
            viewBox="0 0 520 70"
            sx={{
              width: '100%',
              maxWidth: 640,
              bgcolor: tokens.surface,
              borderRadius: 1,
              display: 'block',
              mt: 1.5,
            }}
          >
            {(
              [
                [ui.charts.speed, 'M0 50 C80 20 160 60 260 30 S420 20 520 40'],
                [ui.charts.elevation, 'M0 60 C120 55 200 25 300 35 S440 50 520 20'],
                [ui.charts.cadence, 'M0 35 C100 40 180 30 280 45 S430 30 520 35'],
                [ui.colors.health, 'M0 25 C90 10 200 40 300 15 S440 30 520 10'],
              ] as const
            ).map(([color, d]) => (
              <path key={d} d={d} fill="none" stroke={color} strokeWidth={1.5} />
            ))}
          </Box>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 0.5 }}>
            Heart rate uses the heart rate and health colour (Colours).
          </Typography>
          <Box
            role="img"
            aria-label="Zones preview"
            sx={{
              display: 'flex',
              height: 12,
              mt: 1.5,
              borderRadius: 1,
              overflow: 'hidden',
              maxWidth: 640,
            }}
          >
            {ui.charts.zones.map((z, i) => (
              <Box key={i} sx={{ flex: 1, bgcolor: z }} />
            ))}
          </Box>
        </Section>

        <Section id="other" title="Other settings">
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
            Other settings live where they are used.
          </Typography>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            <Button variant="outlined" onClick={() => navigate('/atlas?open=fusion')}>
              Fusion settings (Atlas)
            </Button>
            <Button variant="outlined" onClick={() => navigate('/atlas?open=import')}>
              Imports and connected sources (Atlas)
            </Button>
          </Box>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 1 }}>
            Google permissions (Gmail, Calendar, Drive) are given when you connect them; to take one
            back, use your Google Account&apos;s{' '}
            <Link
              href="https://myaccount.google.com/connections"
              target="_blank"
              rel="noopener noreferrer"
            >
              third-party connections
            </Link>
            .
          </Typography>
        </Section>
      </Box>
    </Box>
  );
};

export default SettingsPage;
