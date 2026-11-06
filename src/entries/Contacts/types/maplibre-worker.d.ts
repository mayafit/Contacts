/**
 * @fileoverview The MapLibre worker, imported as a file
 * @module Contacts/types/maplibre-worker
 */

/**
 * webpack emits the worker as an asset (the `maplibre-gl-worker` rule in
 * blueFiberSrc/_webpack.bf.common.js) and the import gives its public URL.
 */
declare module 'maplibre-gl/dist/maplibre-gl-worker.mjs' {
  const url: string;
  export default url;
}
