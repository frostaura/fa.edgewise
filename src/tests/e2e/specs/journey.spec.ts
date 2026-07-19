import { expect, test, type Page } from '@playwright/test'

import {
  STORAGE_STATE,
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
  await expect(page.getByRole('heading', { name: 'Cockpit' })).toBeVisible()
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
  await expect(page.getByRole('heading', { name: 'New Plan' })).toBeVisible()

  // 1 — template
  await page.getByRole('button', { name: 'Breakout-Retest' }).click()
  // Template prefills setup tag + trigger.
  await expect(page.getByLabel('Setup tag')).toHaveValue('breakout-retest')

  // 2 — instrument via combobox
  await page.getByRole('combobox', { name: /Search instrument/ }).click()
  await page.getByPlaceholder('Symbol or name…').fill('BTC')
  await page.getByRole('option', { name: /BTC-USD/ }).click()
  await expect(page.getByRole('combobox')).toContainText('BTC-USD')

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

test('trade detail: plan-vs-execution panel and NO_PLAN deduction evidence', async () => {
  await expect(page.getByText('Plan vs execution')).toBeVisible()
  await expect(page.getByText(/Unplanned trade — there is no plan to compare against/)).toBeVisible()
  await expect(page.getByText('Adherence deductions')).toBeVisible()
  await expect(page.getByText('NO_PLAN')).toBeVisible()
  consoleWatcher.assertClean('trade detail')
})

test('journal list shows the unplanned trade with a capped adherence chip', async () => {
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

test('close the trade and see the adherence result', async () => {
  const { tradeId } = loadRunState()
  await page.goto(`/journal/trades/${tradeId}`)
  const closeButton = page.getByRole('button', { name: 'Close trade' })
  if (await closeButton.isVisible()) {
    await closeButton.click()
    await page.getByLabel('Exit price').fill('51000')
    await page.getByRole('dialog').getByRole('button', { name: 'Close trade' }).click()
    await expect(page.getByText('Trade closed — adherence scored')).toBeVisible()
  }
  await expect(page.getByText(/adherence \d+\/100/)).toBeVisible()
  await expect(page.getByText('closed', { exact: true }).first()).toBeVisible()
  consoleWatcher.assertClean('close trade')
})
