# Fluxus Angular frontend

See the [repository README](../../README.md) for database setup, imports, backend startup, and Docker deployment.

With Node 24.15+ within 24.x, run `npm ci`, then `npm start`. The dev server proxies the API to port 5286. `npm test -- --watch=false` runs the component/API-boundary checks; `npm run build` produces `dist/fluxus-web/browser`. Publish the complete browser directory, including `maplibre/` module-worker assets.

Components are standalone and use OnPush, signals, and resource with native fetch. RxJS remains installed as an Angular peer dependency but is not used in application code. MapLibre is loaded on demand.
