/**
 * @fileoverview Echo's own pointer at the Owner's size (AD-41 rule 7): SVG cursors (a dark
 * outline and a light fill, readable on light and dark) and the style sheet that uses them
 * @module Contacts/settings/pointer
 */

import type { PointerSize } from './uiSettings';

/** A cursor drawn on a 24 × 24 grid, and its hot spot on that grid */
interface Shape {
  body: string;
  hot: [number, number];
}

const OUTLINE =
  'fill="#ffffff" stroke="#111827" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round"';

/** The cursors: arrow, hand, open hand, closed hand, crosshair, text */
export const SHAPES: Record<'arrow' | 'hand' | 'grab' | 'grabbing' | 'crosshair' | 'text', Shape> =
  {
    arrow: {
      body: `<path ${OUTLINE} d="M3 2.5 3 19.5 7.6 15.3 10.6 21.8 13.6 20.4 10.7 14.1 17 14.1Z"/>`,
      hot: [3, 2.5],
    },
    hand: {
      body: `<path ${OUTLINE} d="M9 3.5a1.5 1.5 0 0 1 3 0V10h.5V8.5a1.5 1.5 0 0 1 3 0V10h.5V9.5a1.5 1.5 0 0 1 3 0V16c0 3-2 5.5-5 5.5h-2.5c-2 0-3.3-.8-4.3-2.3L4.8 14.6a1.5 1.5 0 0 1 2.5-1.7L9 15Z"/>`,
      hot: [10.5, 2.2],
    },
    grab: {
      body: `<path ${OUTLINE} d="M7 9.5a1.5 1.5 0 0 1 3 0V7.5a1.5 1.5 0 0 1 3 0V9a1.5 1.5 0 0 1 3 0v1a1.5 1.5 0 0 1 3 0V16c0 3-2 5.5-5 5.5h-3c-2.5 0-4-1.2-5-3L4.6 15.4a1.5 1.5 0 0 1 2.4-1.8Z"/>`,
      hot: [12, 12],
    },
    grabbing: {
      body: `<path ${OUTLINE} d="M7 11.5a1.5 1.5 0 0 1 3 0v-.5a1.5 1.5 0 0 1 3 0v.5a1.5 1.5 0 0 1 3 0v.5a1.5 1.5 0 0 1 3 0V16c0 3-2 5.5-5 5.5h-3c-3 0-5-2.5-5-5v-3a1.5 1.5 0 0 1 3 0Z"/>`,
      hot: [12, 13],
    },
    crosshair: {
      body:
        '<path d="M12 2v7M12 15v7M2 12h7M15 12h7" stroke="#111827" stroke-width="4" stroke-linecap="round"/>' +
        '<path d="M12 2v7M12 15v7M2 12h7M15 12h7" stroke="#ffffff" stroke-width="2" stroke-linecap="round"/>',
      hot: [12, 12],
    },
    text: {
      body:
        '<path d="M9 3h6M12 3v18M9 21h6" stroke="#111827" stroke-width="4" stroke-linecap="round"/>' +
        '<path d="M9 3h6M12 3v18M9 21h6" stroke="#ffffff" stroke-width="2" stroke-linecap="round"/>',
      hot: [12, 12],
    },
  };

/**
 * A cursor as a CSS value: the SVG at the size, its hot spot, and the system's cursor of the
 * same kind as the fallback.
 * @param shape - The cursor
 * @param size - Its size in px
 * @param fallback - The system cursor where the image is not used
 * @returns e.g. url("data:image/svg+xml,…") 4 3, default
 */
export const cursorValue = (shape: keyof typeof SHAPES, size: number, fallback: string): string => {
  const { body, hot } = SHAPES[shape];
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24">${body}</svg>`;
  const k = size / 24;
  return `url("data:image/svg+xml,${encodeURIComponent(svg)}") ${Math.round(hot[0] * k)} ${Math.round(
    hot[1] * k,
  )}, ${fallback}`;
};

/**
 * The style sheet of Echo's own pointer; none for the system's.
 * @param size - The pointer size
 * @returns CSS, or null
 */
export const pointerCss = (size: PointerSize): string | null => {
  if (size === 'system') {
    return null;
  }
  const c = (shape: keyof typeof SHAPES, fallback: string) => cursorValue(shape, size, fallback);
  return [
    `html, body { cursor: ${c('arrow', 'default')}; }`,
    `a, button, [role="button"], [role="tab"], [role="menuitem"], [role="option"], label[for], select, summary,` +
      ` .MuiButtonBase-root, .leaflet-interactive, .leaflet-marker-icon, .leaflet-container a` +
      ` { cursor: ${c('hand', 'pointer')} !important; }`,
    `.leaflet-grab, .maplibregl-canvas-container.maplibregl-interactive { cursor: ${c('grab', 'grab')}; }`,
    `.leaflet-dragging .leaflet-grab, .leaflet-dragging .leaflet-grab .leaflet-interactive,` +
      ` .leaflet-dragging .leaflet-marker-draggable, .maplibregl-canvas-container.maplibregl-interactive:active` +
      ` { cursor: ${c('grabbing', 'grabbing')} !important; }`,
    `.atlas-crosshair, .atlas-crosshair .leaflet-grab, .atlas-crosshair .leaflet-interactive, .leaflet-crosshair,` +
      ` .leaflet-crosshair .leaflet-interactive { cursor: ${c('crosshair', 'crosshair')} !important; }`,
    `input:not([type="checkbox"]):not([type="radio"]):not([type="range"]):not([type="color"]), textarea,` +
      ` [contenteditable="true"] { cursor: ${c('text', 'text')}; }`,
  ].join('\n');
};
