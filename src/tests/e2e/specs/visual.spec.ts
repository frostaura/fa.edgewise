import fs from 'node:fs'

import { test, expect, type Browser, type BrowserContext, type Page } from '@playwright/test'

import { SCREENSHOT_DIR, loadRunState } from './helpers'

/**
 * Visual QA screenshots. Depends on the `journey` project having run first:
 * it reuses that run's authenticated storage state and created trade.
 *
 * Coverage grid (kept deliberately small so the committed set stays lean):
 *  - every screen: desktop 1440x900 light + mobile 390x844 light
 *  - dark theme spot-checks on representative screens (desktop + one mobile)
 */

const DESKTOP = { width: 1440, height: 900 }
const MOBILE = { width: 390, height: 844 }

type Theme = 'light' | 'dark'

interface Screen {
  name: string
  path: string | (() => string)
  /** Optional interaction after navigation, before the ready check. */
  prepare?: (page: Page) => Promise<void>
  /** Wait for this locator before shooting. */
  ready: (page: Page) => Promise<void>
  /** Also shoot dark theme (desktop). */
  dark?: boolean
  /** Shoot without auth (login screen). */
  anonymous?: boolean
  /** Skip the mobile shot. */
  desktopOnly?: boolean
}

const visible = (selector: string | ((page: Page) => ReturnType<Page['locator']>)) =>
  async (page: Page) => {
    const locator = typeof selector === 'string' ? page.locator(selector).first() : selector(page)
    await expect(locator).toBeVisible({ timeout: 15_000 })
  }

const heading = (name: string) => visible((page) => page.getByRole('heading', { name }).first())

const SCREENS: Screen[] = [
  { name: 'cockpit', path: '/', ready: heading('Cockpit'), dark: true },
  {
    name: 'journal',
    path: '/journal',
    // The trade renders as a table row (desktop) or card (mobile); the other
    // variant stays hidden, so require a *visible* match.
    ready: visible((p) => p.getByText('BTC-USD').locator('visible=true').first()),
    dark: true,
  },
  {
    name: 'trade-detail',
    path: () => `/journal/trades/${loadRunState().tradeId}`,
    ready: visible((p) => p.getByText('Plan vs execution')),
    dark: true,
  },
  { name: 'new-plan', path: '/journal/plans/new', ready: heading('New Plan') },
  { name: 'inbox', path: '/journal/inbox', ready: heading('Inbox') },
  { name: 'import', path: '/journal/import', ready: heading('Import') },
  { name: 'portfolio', path: '/portfolio', ready: heading('Portfolio') },
  {
    name: 'forecast',
    path: '/portfolio/forecast',
    // Load the forecast the journey saved so the shot shows the bands chart.
    prepare: async (page) => {
      await page.getByRole('combobox').filter({ hasText: 'Load forecast' }).click()
      await page.getByRole('option', { name: 'My forecast' }).click()
    },
    ready: visible((p) => p.getByRole('img', { name: 'Forecast bands by year' })),
    dark: true,
  },
  { name: 'radar', path: '/radar', ready: heading('Radar') },
  {
    name: 'lab-builder',
    path: () => `/lab/strategies/${loadRunState().strategyId ?? ''}`,
    ready: visible((p) => p.getByTestId('condition-row-0')),
  },
  {
    name: 'backtest-report',
    path: '/lab/backtests/00000000-0000-0000-0000-000000000000',
    ready: visible((p) => p.getByText('Backtest not found')),
  },
  {
    name: 'coach-chat',
    path: '/coach/chat',
    ready: heading('Coach'),
  },
  { name: 'settings-risk', path: '/settings/risk', ready: visible((p) => p.getByTestId('risk-profile-Standard')), dark: true },
  {
    name: 'settings-security',
    path: '/settings/security',
    ready: heading('Settings'),
  },
  {
    name: 'login',
    path: '/login',
    ready: visible((p) => p.getByText('Welcome back')),
    dark: true,
    anonymous: true,
  },
]

async function shoot(page: Page, file: string, fullPage: boolean) {
  // Let fonts/transitions settle.
  await page.waitForTimeout(350)
  await page.screenshot({ path: `${SCREENSHOT_DIR}/${file}.png`, fullPage })
}

const API_URL = process.env.EDGEWISE_API_URL ?? 'http://localhost:5210'

/**
 * Refresh tokens rotate on use, so a storage-state file can only authenticate
 * one browser context. Each capture context therefore performs its own API
 * login (issuing an independent refresh token) and seeds localStorage the way
 * the SPA's auth storage expects.
 */
async function apiLogin(): Promise<{ refreshToken: string; user: unknown }> {
  const { email, password } = loadRunState()
  const response = await fetch(`${API_URL}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  if (!response.ok) throw new Error(`visual login failed (${response.status})`)
  const body = (await response.json()) as { refreshToken: string; user: unknown }
  return { refreshToken: body.refreshToken, user: body.user }
}

async function newContextFor(
  browser: Browser,
  screen: Screen,
  viewport: { width: number; height: number },
  theme: Theme,
): Promise<BrowserContext> {
  const context = await browser.newContext({ viewport, colorScheme: theme })
  const auth = screen.anonymous ? null : await apiLogin()
  await context.addInitScript(
    ({ t, session }) => {
      window.localStorage.setItem('edgewise.theme', t)
      if (session) {
        window.localStorage.setItem('edgewise.refreshToken', session.refreshToken)
        window.localStorage.setItem('edgewise.user', JSON.stringify(session.user))
      }
    },
    { t: theme, session: auth },
  )
  return context
}

async function capture(
  browser: Browser,
  screen: Screen,
  viewport: 'desktop' | 'mobile',
  theme: Theme,
) {
  const context = await newContextFor(
    browser,
    screen,
    viewport === 'desktop' ? DESKTOP : MOBILE,
    theme,
  )
  const page = await context.newPage()
  const path = typeof screen.path === 'function' ? screen.path() : screen.path
  await page.goto(path)
  if (screen.prepare) await screen.prepare(page)
  await screen.ready(page)
  await shoot(page, `${screen.name}-${viewport}-${theme}`, viewport === 'desktop')
  await context.close()
}

test.describe('visual QA screenshots', () => {
  test.beforeAll(() => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true })
  })

  for (const screen of SCREENS) {
    test(`shoot ${screen.name}`, async ({ browser }) => {
      await capture(browser, screen, 'desktop', 'light')
      if (!screen.desktopOnly) await capture(browser, screen, 'mobile', 'light')
      if (screen.dark) {
        await capture(browser, screen, 'desktop', 'dark')
        if (screen.name === 'cockpit' || screen.name === 'login') {
          await capture(browser, screen, 'mobile', 'dark')
        }
      }
    })
  }
})
