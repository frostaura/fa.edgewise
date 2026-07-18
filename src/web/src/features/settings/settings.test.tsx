import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Provider } from 'react-redux'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { makeStore } from '@/app/store'
import { allocationWarning, parsePctInput, pctSum } from '@/features/settings/bucketsLogic'
import {
  formatPct,
  formToValues,
  fractionToPct,
  pctToFraction,
  profileToForm,
  riskPresets,
  riskProfileFormSchema,
} from '@/features/settings/riskLogic'
import { SecuritySection } from '@/features/settings/SecuritySection'
import { initialTotpFlow, totpReducer, type TotpFlow } from '@/features/settings/securityLogic'
import type { RiskProfile } from '@/api/riskProfilesApi'

// ------------------------------------------------- fraction ↔ percent

describe('risk form fraction↔pct conversion', () => {
  it('converts wire fractions to display percent and back without float noise', () => {
    expect(fractionToPct(0.0075)).toBe(0.75)
    expect(fractionToPct(0.01)).toBe(1)
    expect(fractionToPct(0.15)).toBe(15)
    expect(pctToFraction(0.75)).toBe(0.0075)
    expect(pctToFraction(1)).toBe(0.01)
    // The classic float trap: 28.35 / 100 = 0.28349999…
    expect(pctToFraction(28.35)).toBe(0.2835)
    for (const pct of [0.05, 0.5, 1, 2.5, 33.33, 99]) {
      expect(fractionToPct(pctToFraction(pct))).toBe(pct)
    }
  })

  it('round-trips a profile through form values', () => {
    const profile: RiskProfile = {
      id: 'p1',
      name: 'Standard',
      version: 3,
      isActive: true,
      riskPct: 0.01,
      heatCapPct: 0.04,
      clusterCapPct: 0.02,
      dailyStopPct: 0.03,
      dailyLossCountStop: 3,
      weeklyStopPct: 0.06,
      maxLeverage: 2,
      minRR: 1.5,
      maxPositions: 5,
      ladderThresholds: { riskHalvedPct: 0.05, pausedPct: 0.1, paperPct: 0.15 },
    }
    const form = profileToForm(profile)
    expect(form.riskPct).toBe(1)
    expect(form.heatCapPct).toBe(4)
    expect(form.riskHalvedPct).toBe(5)

    const values = formToValues(form)
    expect(values.riskPct).toBe(0.01)
    expect(values.weeklyStopPct).toBe(0.06)
    expect(values.ladderThresholds).toEqual({ riskHalvedPct: 0.05, pausedPct: 0.1, paperPct: 0.15 })
    expect(values.maxLeverage).toBe(2)
  })

  it('presets pass schema validation and mirror the seeded profiles', () => {
    for (const preset of Object.values(riskPresets)) {
      expect(riskProfileFormSchema.safeParse({ name: 'X', ...preset }).success).toBe(true)
    }
    expect(formToValues({ name: 'X', ...riskPresets.Conservative }).riskPct).toBe(0.005)
    expect(formToValues({ name: 'X', ...riskPresets.Aggressive }).heatCapPct).toBe(0.06)
  })

  it('rejects a mis-ordered ladder and risk above the heat cap', () => {
    const base = { name: 'X', ...riskPresets.Standard }
    expect(
      riskProfileFormSchema.safeParse({ ...base, riskHalvedPct: 12, pausedPct: 10, paperPct: 15 }).success,
    ).toBe(false)
    expect(riskProfileFormSchema.safeParse({ ...base, riskPct: 5, heatCapPct: 4 }).success).toBe(false)
    expect(riskProfileFormSchema.safeParse({ ...base, riskPct: 0 }).success).toBe(false)
    expect(riskProfileFormSchema.safeParse({ ...base, maxPositions: 2.5 }).success).toBe(false)
  })

  it('formats fractions as tidy percent strings', () => {
    expect(formatPct(0.01)).toBe('1%')
    expect(formatPct(0.0075)).toBe('0.75%')
    expect(formatPct(0.15)).toBe('15%')
  })
})

// ------------------------------------------------- allocation warning

describe('bucket allocation-sum warning', () => {
  it('sums fractions to percent, swallowing float noise', () => {
    expect(pctSum([0.6, 0.25, 0.05, 0.1])).toBe(100)
    expect(pctSum([0.1, 0.2, 0.3])).toBe(60)
  })

  it('warns only when allocations do not sum to 100%', () => {
    expect(allocationWarning([0.6, 0.25, 0.05, 0.1], 'Target allocations')).toBeNull()
    expect(allocationWarning([], 'Target allocations')).toBeNull()
    expect(allocationWarning([0.6, 0.25, 0.05], 'Target allocations')).toBe(
      'Target allocations sum to 90%, not 100%.',
    )
    expect(allocationWarning([0.7, 0.5], 'Contribution splits')).toBe(
      'Contribution splits sum to 120%, not 100%.',
    )
  })

  it('parses percent inputs to fractions and rejects junk', () => {
    expect(parsePctInput('25')).toBe(0.25)
    expect(parsePctInput('0')).toBe(0)
    expect(parsePctInput('100')).toBe(1)
    expect(parsePctInput('101')).toBeNull()
    expect(parsePctInput('-1')).toBeNull()
    expect(parsePctInput('abc')).toBeNull()
  })
})

// ---------------------------------------------------- TOTP flow reducer

describe('TOTP flow reducer', () => {
  it('walks the happy enrolment path and drops the secret once enabled', () => {
    let state: TotpFlow = initialTotpFlow

    state = totpReducer(state, { type: 'begin-setup' })
    expect(state.busy).toBe(true)

    state = totpReducer(state, { type: 'setup-ready', secret: 'ABC123', otpauthUri: 'otpauth://totp/x' })
    expect(state).toMatchObject({ step: 'setup', secret: 'ABC123', busy: false, error: null })

    state = totpReducer(state, { type: 'code-input', code: '123456' })
    state = totpReducer(state, { type: 'submit' })
    expect(state.busy).toBe(true)

    state = totpReducer(state, { type: 'enabled', recoveryCodes: ['AAAA-BBBB', 'CCCC-DDDD'] })
    expect(state.step).toBe('recovery')
    expect(state.recoveryCodes).toEqual(['AAAA-BBBB', 'CCCC-DDDD'])
    expect(state.secret).toBeNull() // never keep the secret around
    expect(state.otpauthUri).toBeNull()

    state = totpReducer(state, { type: 'cancel' })
    expect(state).toEqual(initialTotpFlow)
  })

  it('surfaces failures without losing the entered state, and clears the error on typing', () => {
    let state = totpReducer(initialTotpFlow, {
      type: 'setup-ready',
      secret: 'S',
      otpauthUri: 'otpauth://totp/x',
    })
    state = totpReducer(state, { type: 'code-input', code: '000000' })
    state = totpReducer(state, { type: 'submit' })
    state = totpReducer(state, { type: 'failed', error: 'Invalid TOTP code.' })
    expect(state).toMatchObject({ step: 'setup', busy: false, error: 'Invalid TOTP code.', code: '000000' })

    state = totpReducer(state, { type: 'code-input', code: '1' })
    expect(state.error).toBeNull()
  })

  it('handles the disable path', () => {
    let state = totpReducer(initialTotpFlow, { type: 'begin-disable' })
    expect(state.step).toBe('disable')
    state = totpReducer(state, { type: 'code-input', code: 'AAAA-BBBB' })
    state = totpReducer(state, { type: 'submit' })
    state = totpReducer(state, { type: 'disabled' })
    expect(state).toEqual(initialTotpFlow)
  })
})

// ------------------------------------------- security section (mock fetch)

const me = {
  id: 'u1',
  email: 'trader@test.local',
  baseCurrency: 'ZAR',
  timezone: 'Africa/Johannesburg',
  totpEnabled: false,
  isAdmin: false,
  llmOptOut: false,
  settingsJson: null,
  createdAt: '2026-01-01T00:00:00Z',
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}

describe('SecuritySection TOTP enrolment (mock fetch)', () => {
  beforeEach(() => {
    vi.spyOn(window, 'fetch').mockImplementation(async (input) => {
      const url = typeof input === 'string' ? input : (input as Request).url
      if (url.includes('/api/auth/totp/setup')) {
        return jsonResponse({ secret: 'JBSWY3DPEHPK3PXP', otpauthUri: 'otpauth://totp/Edgewise:trader?secret=JBSWY3DPEHPK3PXP' })
      }
      if (url.includes('/api/auth/totp/enable')) {
        return jsonResponse({ recoveryCodes: ['AAAA-1111', 'BBBB-2222'] })
      }
      if (url.includes('/api/me/tokens')) return jsonResponse([])
      if (url.includes('/api/me')) return jsonResponse(me)
      return new Response(null, { status: 404 })
    })
  })

  it('runs setup → QR + secret → verify → recovery codes shown once', async () => {
    const user = userEvent.setup()
    render(
      <Provider store={makeStore()}>
        <MemoryRouter>
          <SecuritySection />
        </MemoryRouter>
      </Provider>,
    )

    await user.click(await screen.findByRole('button', { name: /enable two-factor auth/i }))

    // Setup response rendered: manual secret + QR + code input.
    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument()
    expect(screen.getByLabelText(/totp enrolment qr code/i)).toBeInTheDocument()

    await user.type(screen.getByLabelText(/6-digit code/i), '123456')
    await user.click(screen.getByRole('button', { name: /verify & enable/i }))

    // Recovery codes appear exactly once, with copy-all and confirm actions.
    const codes = await screen.findByTestId('recovery-codes')
    expect(codes).toHaveTextContent('AAAA-1111')
    expect(codes).toHaveTextContent('BBBB-2222')
    expect(screen.getByRole('button', { name: /copy all/i })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /i saved them/i }))
    expect(screen.queryByTestId('recovery-codes')).not.toBeInTheDocument()
  })
})
