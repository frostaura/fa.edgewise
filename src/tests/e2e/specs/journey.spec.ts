import { expect, test, type Page } from '@playwright/test'

import {
  loadRunState,
  saveRunState,
  uniqueEmail,
  watchConsole,
  type ConsoleWatcher,
} from './helpers'

/**
 * The single sequential user journey through the whole app, from a clean
 * database: register -> cockpit -> risk settings -> plan -> quick-log an
 * unplanned trade -> journal -> trade detail -> close -> portfolio ->
 * forecast -> radar -> lab -> coach -> settings -> dark mode -> logout/login.
 *
 * All tests share one page (serial mode); state accumulates on purpose.
 */
test.describe.configure({ mode: 'serial' })

let page: Page
let consoleWatcher: ConsoleWatcher

const password = 'e2e-password-1'
const email = uniqueEmail()

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage()
  consoleWatcher = watchConsole(page)
})

test.afterAll(async () => {
  await page.close()
})

test('register a new account and land authenticated on the cockpit', async () => {
  await page.goto('/register')
  await expect(page.getByText('Create your account')).toBeVisible()

  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()

  // Lands authenticated on the cockpit.
  await expect(page).toHaveURL('/')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Cockpit' })).toBeVisible()
  saveRunState({ email, password })
  consoleWatcher.assertClean('register')
})

test('cockpit shows the five reads cards', async () => {
  for (const label of ['Heat', 'Daily P&L', 'Ladder', 'Calendar', 'State']) {
    await expect(page.getByText(label, { exact: true }).first()).toBeVisible()
  }
  // Plan gate is unlocked on a clean account.
  await expect(page.getByRole('link', { name: 'New Plan' })).toBeVisible()
  consoleWatcher.assertClean('cockpit')
})

test('settings/risk shows the seeded profiles with Standard active', async () => {
  await page.goto('/settings/risk')
  for (const name of ['Standard', 'Conservative', 'Aggressive']) {
    await expect(page.getByTestId(`risk-profile-${name}`)).toBeVisible()
  }
  await expect(page.getByTestId('risk-profile-Standard')).toContainText('Active')
  consoleWatcher.assertClean('settings/risk')
})

test('new plan: template + instrument + six fields, size preview responds, submit', async () => {
  await page.goto('/journal/plans/new')
  await expect(page.getByRole('main').getByRole('heading', { name: 'New Plan' })).toBeVisible()

  // 1 — template
  await page.getByRole('button', { name: 'Breakout-Retest' }).click()
  // Template prefills setup tag + trigger.
  await expect(page.getByLabel('Setup tag')).toHaveValue('breakout-retest')

  // 2 — instrument via combobox (its accessible name is empty, match by text)
  const instrumentPicker = page.getByRole('combobox').filter({ hasText: 'Search instrument' })
  await instrumentPicker.click()
  await page.getByPlaceholder('Symbol or name…').fill('BTC')
  await page.getByRole('option', { name: /BTC-USD/ }).click()
  await expect(page.getByRole('combobox').filter({ hasText: 'BTC-USD' })).toBeVisible()

  // 3 — the six fields
  await page.getByLabel('Planned entry price').fill('50000')
  await page.getByLabel('Stop price').fill('48000')
  await page.getByLabel('Trigger').fill('Retest of 50k holds on the 1h close')
  await page.getByLabel('Invalidation').fill('Hourly close back below 48k')
  await page.getByLabel('Target rule (JSON or free text)').fill('2R runner')

  // Size auto-calc: the preview card switches from the hint to a suggestion.
  const sizing = page.getByText('Suggested qty')
  await expect(sizing).toBeVisible()
  await expect(page.getByText(/1R =/)).toBeVisible()

  // Bucket equity of a fresh account may be 0 -> suggested qty 0. Type an
  // explicit size so the plan is submittable either way.
  await page.getByLabel(/Size \(qty\)/).fill('0.1')

  // 4 — confirm the whole checklist
  const checkboxes = page.getByRole('checkbox')
  const count = await checkboxes.count()
  expect(count).toBeGreaterThan(0)
  for (let i = 0; i < count; i += 1) {
    await checkboxes.nth(i).check()
  }

  // 5 — submit
  await page.getByRole('button', { name: 'Create plan' }).click()
  await expect(page.getByText(/Plan for BTC-USD is live/)).toBeVisible()
  await expect(page).toHaveURL(/\/journal\?plan=/)
  consoleWatcher.assertClean('new plan')
})

test('quick log an unplanned fill and confess it into a trade', async () => {
  await page.goto('/journal')
  await page.getByRole('button', { name: 'Quick Log' }).click()
  await expect(page.getByRole('dialog')).toContainText('Quick Log')

  await page.getByRole('combobox', { name: 'Instrument' }).click()
  await page.getByRole('option', { name: /BTC-USD/ }).click()
  // Side defaults to buy; fill qty/price/fee.
  await page.getByLabel('Quantity').fill('0.1')
  await page.getByLabel('Price', { exact: true }).fill('50000')
  await page.getByLabel('Fees').fill('5')
  await page.getByRole('button', { name: 'Log trade' }).click()

  await expect(page.getByText('Trade logged as unplanned')).toBeVisible()
  // Confession navigates straight to the trade detail page.
  await expect(page).toHaveURL(/\/journal\/trades\//)
  const tradeId = page.url().split('/journal/trades/')[1]
  const state = loadRunState()
  saveRunState({ ...state, tradeId })
  consoleWatcher.assertClean('quick log')
})

test('trade detail: plan-vs-execution panel for the unplanned open trade', async () => {
  await expect(page.getByText('Plan vs execution')).toBeVisible()
  await expect(page.getByText(/Unplanned trade — there is no plan to compare against/)).toBeVisible()
  await expect(page.getByText('Adherence deductions')).toBeVisible()
  // Adherence is only computed at close time.
  await expect(page.getByText('Scored when the trade closes.')).toBeVisible()
  consoleWatcher.assertClean('trade detail')
})

test('close the trade: adherence scored with NO_PLAN deduction evidence', async () => {
  const { tradeId } = loadRunState()
  await page.goto(`/journal/trades/${tradeId}`)
  await page.getByRole('button', { name: 'Close trade' }).click()
  await page.getByLabel('Exit price').fill('51000')
  await page.getByRole('dialog').getByRole('button', { name: 'Close trade' }).click()
  await expect(page.getByText('Trade closed — adherence scored')).toBeVisible()

  await expect(page.getByText(/adherence \d+\/100/)).toBeVisible()
  await expect(page.getByText('closed', { exact: true }).first()).toBeVisible()
  // The deduction list carries the NO_PLAN evidence for confessed trades.
  await expect(page.getByText('NO_PLAN')).toBeVisible()
  consoleWatcher.assertClean('close trade')
})

test('journal list shows the trade with a capped adherence chip', async () => {
  await page.goto('/journal')
  const row = page.getByRole('row').filter({ hasText: 'BTC-USD' }).first()
  await expect(row).toBeVisible()
  await expect(row).toContainText('unplanned')
  // Unplanned cap is 60 -> C at best.
  const chip = row.locator('[data-slot="adherence-chip"]')
  await expect(chip).toBeVisible()
  const grade = await chip.getAttribute('data-grade')
  expect(['C', 'D', 'F']).toContain(grade)
  consoleWatcher.assertClean('journal list')
})

test('portfolio renders (empty state is fine)', async () => {
  await page.goto('/portfolio')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Portfolio' })).toBeVisible()
  // Fresh account: valuations have not landed, so the empty state is expected.
  await expect(page.getByText(/No holdings yet|Trading/).first()).toBeVisible()
  consoleWatcher.assertClean('portfolio')
})

test('forecast studio: manual assumption row + deterministic run renders bands', async () => {
  await page.goto('/portfolio/forecast')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Forecast Studio' })).toBeVisible()

  // Manual assumption row (fresh accounts have nothing to seed from).
  await page.getByLabel('New asset name').fill('BTC')
  await page.getByRole('button', { name: 'Add asset' }).click()
  await expect(page.getByText('BTC', { exact: true })).toBeVisible()

  // A yearly contribution, fully split into BTC, makes the projection non-flat.
  await page.getByLabel('Contribution / year').fill('12000')
  await page.getByLabel('BTC contribution split').fill('100')

  // Deterministic run (Monte Carlo off by default).
  await page.getByRole('button', { name: 'Run' }).click()
  await expect(page.getByText('Forecast saved.')).toBeVisible()
  await expect(page.getByRole('img', { name: 'Forecast bands by year' })).toBeVisible()
  await expect(page.getByText(/Deterministic bear\/base\/bull/)).toBeVisible()
  consoleWatcher.assertClean('forecast')
})

test('radar: create watchlist, add an instrument, open the alert manager', async () => {
  await page.goto('/radar')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Radar' })).toBeVisible()

  // Watchlist create
  await page.getByPlaceholder('New watchlist name').fill('Stalking')
  await page.getByRole('button', { name: 'Add list' }).click()
  await expect(page.getByText('Stalking', { exact: true })).toBeVisible()

  // Add an instrument to it
  await page.getByRole('button', { name: 'Add', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('Add instrument')
  await page.getByRole('combobox').filter({ hasText: 'Choose an instrument' }).click()
  await page.getByRole('option', { name: /ETH-USD/ }).click()
  await page.getByLabel('Why watching').fill('Waiting for the range to resolve')
  await page.getByRole('button', { name: 'Add to watchlist' }).click()
  await expect(page.getByRole('dialog')).toBeHidden()
  await expect(page.getByText('ETH-USD')).toBeVisible()

  // Alert manager opens
  await page.getByRole('tab', { name: 'Alerts' }).click()
  await page.getByRole('button', { name: 'New alert' }).click()
  await expect(page.getByRole('dialog')).toContainText('Create alert')
  await page.keyboard.press('Escape')
  consoleWatcher.assertClean('radar')
})

test('lab: create a strategy from a template, builder rows visible, save', async () => {
  await page.goto('/lab/strategies')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Strategies' })).toBeVisible()

  await page.getByRole('button', { name: 'New strategy' }).click()
  await expect(page.getByRole('dialog')).toContainText('New strategy')
  await page.getByLabel('Name').fill('E2E trend pullback')
  // Pick the first non-blank template when one is shipped; Blank otherwise.
  const templateRadios = page.getByRole('dialog').getByRole('radio')
  const radioCount = await templateRadios.count()
  await templateRadios.nth(radioCount > 1 ? 1 : 0).check()
  await page.getByRole('button', { name: 'Create', exact: true }).click()

  // Lands in the builder with at least one condition row.
  await expect(page).toHaveURL(/\/lab\/strategies\//)
  await expect(page.getByRole('main').getByRole('heading', { name: 'E2E trend pullback' })).toBeVisible()
  await expect(page.getByTestId('condition-row-0')).toBeVisible()
  saveRunState({ ...loadRunState(), strategyId: page.url().split('/lab/strategies/')[1] })

  // Touch the tree then save a new version.
  await page.getByRole('button', { name: 'Add condition' }).click()
  await page.getByRole('button', { name: /Save \(new version\)/ }).click()
  await expect(page.getByText(/Saved as version|Saved \(no rule changes/)).toBeVisible()
  consoleWatcher.assertClean('lab builder')
})

test('coach chat shows the offline card linking to analytics', async () => {
  await page.goto('/coach/chat')
  // No LLM key on this server: the offline card appears once a message is sent.
  const input = page.getByPlaceholder('Ask about your trading…')
  await input.fill('How is my process this week?')
  await page.getByRole('button', { name: 'Send' }).click()
  await expect(page.getByText('Coach is offline')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open journal analytics' })).toBeVisible()
  consoleWatcher.assertClean('coach chat')
})

test('all settings sections render', async () => {
  const sections = [
    { path: '/settings/profile', marker: /Profile|Email/ },
    { path: '/settings/risk', marker: /Risk \/ trade/ },
    { path: '/settings/buckets', marker: /Bucket|Trading/ },
    { path: '/settings/integrations', marker: /Polymarket|Binance|exchange|Exchange|account/i },
    { path: '/settings/llm', marker: /LLM|coach|Coach|model/ },
    { path: '/settings/security', marker: /password|Password|two-factor|Two-factor|TOTP/i },
    { path: '/settings/export', marker: /export|Export|download|Download/ },
  ]
  for (const section of sections) {
    await page.goto(section.path)
    await expect(page.getByRole('main').getByRole('heading', { name: 'Settings' })).toBeVisible()
    await expect(page.getByText(section.marker).first()).toBeVisible()
  }
  consoleWatcher.assertClean('settings sections')
})

test('dark mode toggle flips the theme and persists', async () => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Switch to dark theme' }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await page.reload()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await page.getByRole('button', { name: 'Switch to light theme' }).click()
  await expect(page.locator('html')).not.toHaveClass(/dark/)
  consoleWatcher.assertClean('theme toggle')
})

test('logout then login roundtrip', async () => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Account menu' }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page).toHaveURL(/\/login/)

  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL('/')
  await expect(page.getByRole('main').getByRole('heading', { name: 'Cockpit' })).toBeVisible()
  consoleWatcher.assertClean('logout/login')
})
