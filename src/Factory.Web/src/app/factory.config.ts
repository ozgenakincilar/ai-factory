// Local development settings for the Product Workspace. API and SignalR calls are same-origin and
// proxied to the Factory API by the dev server (proxy.conf.json), so no CORS is involved.
export const factoryConfig = {
  issuer: 'http://127.0.0.1:58080/realms/factory-dev',
  clientId: 'factory-web',
  apiPrefix: '/api',
  hubPath: '/hubs/workflows',
} as const;
