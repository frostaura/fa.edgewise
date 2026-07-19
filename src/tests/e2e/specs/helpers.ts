import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { expect, type Page } from '@playwright/test'

const here = path.dirname(fileURLToPath(import.meta.url))

/** Shared scratch state between the journey and visual projects. */
export const STATE_DIR = path.join(here, '..', '.auth')
export const RUN_STATE = path.join(STATE_DIR, 'run-state.json')
export const SCREENSHOT_DIR = path.join(here, '..', 'screenshots')

export interface RunState {
  email: string
  password: string
  tradeId?: string
  strategyId?: string
}

export function saveRunState(state: RunState): void {
  fs.mkdirSync(STATE_DIR, { recursive: true })
  fs.writeFileSync(RUN_STATE, JSON.stringify(state, null, 2))
}

export function loadRunState(): RunState {
  return JSON.parse(fs.readFileSync(RUN_STATE, 'utf8')) as RunState
}

/**
 * Console-noise policy. Anything matching is tolerated; every other
 * `console.error` / pageerror fails the journey at the page assertion points.
 *
 * Known-acceptable noise:
 *  - RTK Query surfaces handled 4xx responses from optional endpoints (market
 *    search, decisions) as rejected actions; the UI degrades gracefully.
 *  - Failed resource loads for optional endpoints (401 right after logout,
 *    404 for not-yet-shipped verticals) are handled in the UI.
 *  - React DevTools advertisement and Vite HMR chatter are dev-server-only.
 */
const IGNORED_CONSOLE_PATTERNS: RegExp[] = [
  /Failed to load resource.*40[134]/i,
  /Failed to load resource.*status of 4\d\d/i,
  /React DevTools/i,
  /\[vite\]/i,
  /Download the Vue Devtools/i,
]

export interface ConsoleWatcher {
  errors: string[]
  /** Assert nothing unexpected was logged since the last check, then reset. */
  assertClean: (context: string) => void
}

/** Collect console errors + uncaught exceptions on a page. */
export function watchConsole(page: Page): ConsoleWatcher {
  const errors: string[] = []
  page.on('console', (msg) => {
    if (msg.type() !== 'error') return
    const text = msg.text()
    if (IGNORED_CONSOLE_PATTERNS.some((re) => re.test(text))) return
    errors.push(text)
  })
  page.on('pageerror', (err) => {
    errors.push(`pageerror: ${err.message}`)
  })
  return {
    errors,
    assertClean(context: string) {
      expect(errors, `unexpected console errors on ${context}`).toEqual([])
      errors.length = 0
    },
  }
}

/** Unique per-run registration email. */
export function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.floor(Math.random() * 10_000)}@example.com`
}
