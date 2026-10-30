# Epic 14 — Settings and appearance: Echo the way the Owner can read and grab it

> Vision: the Owner sets the size of each kind of text, each kind of map symbol and
> the pointer, and the colour and style of each kind of thing, sees the result at
> once, and finds the same Echo on every device.

Requirements: PRD FR-68 to FR-71 (`echo/prd.md` §4.14). Architecture: AD-41
(`echo/architecture.md` §5C).

## Why

On 2026-10-30 the Owner found the map's points too small to grab, the pointer too
small, and the text too small, but did not want everything simply enlarged. Today
every size and colour is a constant: the theme's typography, about 115 font sizes
set outside it, every radius and line style in `AtlasMap.tsx` and `routeGeometry.ts`,
the type colours in `types.ts`, the chart colours.

## Stories

### 14.1 The appearance document and its schema — Phase 1

- [x] `atlas.owner_settings` (migration `OwnerSettings`) and `/api/settings/{kind}`
  (kind `ui`; GET, PUT up to 64 KB, DELETE), per Owner.
- [x] `uiSettings.ts`: defaults (today's Echo), ranges, merge and clamp.
- [x] `SettingsProvider`: the browser copy first, then the Owner's document; `useUi()`;
  saving 600 ms after the last change.

### 14.2 Text sizes — Phase 1

- [x] The theme's typography from the text sizes, rebuilt on change.
- [x] Every font size set outside the theme goes through `fs` (small, body, headings,
  charts, map, navigation), with the CSS variables on the root.

### 14.3 Map symbols, grabbing and the pointer — Phase 1

- [x] Every symbol size on the map from the settings; the canvas renderer's tolerance
  from the grab distance.
- [x] Echo's own pointer at 24, 32 or 48 px (arrow, hand, grab, grabbing, crosshair,
  text); marking and drawing use the `atlas-crosshair` class.

### 14.4 Colours and route styles — Phase 1

- [x] Type, entity and chart colours from the settings everywhere (map, density,
  legend, lists, panels, charts).
- [x] Route styles per type, the selected and dimmed routes, and the pieces (observed,
  inferred, gap) with patterns in proportion to the width.

### 14.5 The Settings page — Phase 1

- [x] `/settings` from a gear at the bottom of the rail (not an Area).
- [x] Sections Text, Pointer, Map symbols, Colours, Routes, Charts, Other settings;
  each control with its default; Reset per section and Reset everything; previews;
  the saving line.

### 14.7 Review fixes — Phase 1

- [x] A change the browser kept but never saved (failed save, closed tab) wins over the
  account's older copy and is saved; a waiting save is sent on pagehide.
- [x] Sizes and the pointer applied before the first paint; side effects out of the state
  updater; the theme rebuilt only for text sizes; a late subscriber gets the settings.
- [x] Filter chips follow the area colour; boxes with text grow (minHeight), the rail
  widens; icons up to 20.5 px follow body text; one heart-rate colour.
- [x] Pointer on sliders, selects, comboboxes and input roots, not on disabled
  controls; `--echo-hand` for inline hands.
- [x] Grab distance refreshes line bounds; Leaflet text keeps its rem/em sizes.
- [x] Unique labels per route type, value text on sliders, defaults on every control,
  the chart lines preview; two first saves at once on the server.

### 14.6 Tests — Phase 1

- [x] Pure: merge and clamp (unknown keys, out of range, bad colours, old
  documents), `fs`, patterns, the theme's typography.
- [x] Postgres: the document per Owner, the size limit, the kind, isolation.
- [x] Jest: the page's controls, reset, saving, a11y; the map and charts read the
  settings.

## Coverage

| Requirement | Stories |
| --- | --- |
| FR-68 The Settings page | 14.1, 14.5, 14.6 |
| FR-69 Text sizes | 14.2, 14.6 |
| FR-70 Map symbols, grabbing and the pointer | 14.3, 14.6 |
| FR-71 Colours and route styles | 14.4, 14.6 |
