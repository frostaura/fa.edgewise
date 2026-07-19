import fs from 'node:fs'

import { defineConfig, devices } from '@playwright/test'

/**
 * Edgewise end-to-end suite.
 *
 * Expects the full stack to be running locally:
 *   - API on http://localhost:5210 (fresh `edgewise_e2e` database recommended)
 *   - Vite dev server on http://localhost:5173 (proxies /api -> :5210)
 *
 * See README.md in this directory for the exact boot commands.
 *
 * The suite is a single sequential journey (register -> plan -> trade -> ...)
 * so it runs with one worker and no parallelism. The `visual` project reuses
 * the auth state + seed trade created by the journey and only takes
 * screenshots, so it must run after `journey` (enforced via project deps).
 */

// Chromium is preinstalled outside playwright's managed cache in this
// environment; fall back to the standard resolution elsewhere.
const CHROMIUM = process.env.EDGEWISE_CHROMIUM ?? '/opt/pw-browsers/chromium'
const executablePath = fs.existsSync(CHROMIUM) ? CHROMIUM : undefined

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env.EDGEWISE_WEB_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: executablePath ? { executablePath } : {},
  },
  projects: [
    {
      name: 'journey',
      testMatch: /journey\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
      },
    },
    {
      name: 'visual',
      testMatch: /visual\.spec\.ts/,
      dependencies: ['journey'],
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
      },
    },
  ],
})
