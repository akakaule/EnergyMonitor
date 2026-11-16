import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

// The built app is served by the ASP.NET Core host from its wwwroot, so API calls are same-origin.
// In development, Vite proxies them to the locally running host instead.
const api = process.env.API_URL ?? 'http://localhost:5090';

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['icons/apple-touch-icon.png', 'icons/favicon.svg'],
      manifest: {
        name: 'Energy Monitor',
        short_name: 'Energi',
        description: 'Elforbrug, varmepumpe og alarmer for hjem og sommerhus',
        lang: 'da',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        background_color: '#f9f9f7',
        theme_color: '#184f95',
        icons: [
          { src: 'icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: 'icons/icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        // Only the app shell is cached. Consumption data is private and always fetched live.
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/auth\//, /^\/health/, /^\/alive/],
        runtimeCaching: [],
      },
    }),
  ],
  build: {
    outDir: '../EnergyMonitor.Web/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': api,
      '/auth': api,
    },
  },
  test: {
    environment: 'jsdom',
    globals: false,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
