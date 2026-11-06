/**
 * Loads MapLibre GL and tells it where its worker is.
 *
 * MapLibre 6 ships as ES modules only and finds its worker through
 * `import.meta.url`, which a bundler rewrites, so under webpack it must be
 * told the worker's URL once. The worker is imported as a file: webpack emits
 * it as an asset (the `maplibre-gl-worker` rule in
 * blueFiberSrc/_webpack.bf.common.js) and the import gives its URL under the
 * public path. It is a same-origin `.js` file, so the CSP needs no `blob:` and
 * nginx serves it as JavaScript.
 *
 * Kept in its own module, loaded with `import()` next to the Leaflet plugin,
 * so MapLibre stays out of the main bundle and out of Jest.
 */
import { setWorkerUrl } from 'maplibre-gl';
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs';

setWorkerUrl(new URL(workerUrl, window.location.href).href);
