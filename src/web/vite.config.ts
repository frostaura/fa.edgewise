/// <reference types="vitest/config" />
import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'

// ---------------------------------------------------------------------------
// Dev API proxy
// ---------------------------------------------------------------------------
// The Edgewise .NET backend listens on http://localhost:5210 during local
// development. Every request the SPA makes to /api/* is proxied there by the
// Vite dev server, so the frontend can always call relative "/api" URLs.
// If the backend port changes, update DEV_API_TARGET below.
const DEV_API_TARGET = 'http://localhost:5210'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.svg'],
      manifest: {
        name: 'Edgewise',
        short_name: 'Edgewise',
        description: 'Process-first trading journal, portfolio and AI coach.',
        start_url: '/',
        display: 'standalone',
        // Tokens: deep-slate background (--background dark) + dark surface theme.
        background_color: '#0e1420',
        theme_color: '#0e1420',
        icons: [
          { src: '/pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: '/pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          {
            src: '/pwa-maskable-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,ico,woff2}'],
        navigateFallbackDenylist: [/^\/api\//],
      },
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, 'src'),
    },
  },
  server: {
    proxy: {
      '/api': {
        target: DEV_API_TARGET,
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    css: false,
    restoreMocks: true,
  },
})
