# Factory.Web — Product Workspace

Angular 22 web app of the Universal AI Factory (M12a slice). Scope, local setup, security notes,
verification evidence and limits: [docs/PRODUCT_WORKSPACE.md](../../docs/PRODUCT_WORKSPACE.md).

- `npm start` — dev server on http://127.0.0.1:4200, proxying `/api` and `/hubs` to the API on 5168.
- `npm test -- --watch=false` — unit tests (Vitest).
- `npm run build` — production build into `dist/`.
