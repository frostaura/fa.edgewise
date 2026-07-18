import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { CockpitStatus } from '@/api/cockpitApi'
import { StatusCards, buildReadCards } from '@/features/cockpit/StatusCards'
import {
  canSubmitOverride,
  initialOverrideFlow,
  overrideFlowReducer,
  planLock,
  statusTone,
} from '@/features/cockpit/cockpitLock'

function makeStatus(overrides?: Partial<CockpitStatus['reads']>): CockpitStatus {
  return {
    reads: {
      heat: {
        openRiskMajor: 120,
        heatPct: 1.5,
        capPct: 4,
        estimated: false,
        openTradesCount: 2,
        status: 'green',
        detail: 'Heat 1.5% of equity, cap 4%.',
      },
      dailyPnl: {
        pnlMinor: -1500,
        realisedR: -1,
        lossCount: 1,
        lossCountStop: 3,
        stopMinor: 6000,
        progressPct: 25,
        tripped: false,
        status: 'green',
        detail: 'Fine.',
      },
      ladder: {
        drawdownPct: 2,
        rung: 'full',
        nextRungPct: 5,
        locked: false,
        status: 'green',
        detail: 'Full size.',
      },
      calendar: { events: [], status: 'green', detail: 'No catalysts in the next 24h.' },
      state: { state: 'calm', declaredAt: null, status: 'green', detail: 'Calm.' },
      ...overrides,
    },
    allGreen: true,
    newPlanUnlocked: true,
    activeOverride: null,
    today: { realisedR: -1, pnlMinor: -1500, tradesCount: 2, dailyStopProgressPct: 25 },
  }
}

describe('five-reads status card mapping', () => {
  it('renders five cards with their statuses', () => {
    const status = makeStatus({
      dailyPnl: {
        pnlMinor: -7000,
        realisedR: -3,
        lossCount: 3,
        lossCountStop: 3,
        stopMinor: 6000,
        progressPct: 100,
        tripped: true,
        status: 'red',
        detail: 'Circuit breaker: daily stop hit.',
      },
      calendar: {
        events: [
          {
            id: 'e1',
            kind: 'earnings',
            severity: 'red',
            title: 'AAPL earnings',
            at: new Date().toISOString(),
            held: true,
            watched: false,
          },
        ],
        status: 'amber',
        detail: '1 red catalyst inside the next 24h.',
      },
    })

    render(<StatusCards status={status} />)

    const cards = document.querySelectorAll('[data-slot="cockpit-read"]')
    expect(cards).toHaveLength(5)
    expect(document.querySelectorAll('[data-status="green"]')).toHaveLength(3)
    expect(document.querySelectorAll('[data-status="amber"]')).toHaveLength(1)
    expect(document.querySelectorAll('[data-status="red"]')).toHaveLength(1)

    expect(screen.getByText('Heat')).toBeInTheDocument()
    expect(screen.getByText('Daily P&L')).toBeInTheDocument()
    expect(screen.getByText('Ladder')).toBeInTheDocument()
    expect(screen.getByText('Calendar')).toBeInTheDocument()
    expect(screen.getByText('State')).toBeInTheDocument()
  })

  it('maps read values into card values', () => {
    const cards = buildReadCards(makeStatus())
    expect(cards.map((c) => c.key)).toEqual(['heat', 'dailyPnl', 'ladder', 'calendar', 'state'])
    expect(cards[0].value).toBe('1.5%')
    expect(cards[1].value).toBe('-15.00')
    expect(cards[2].value).toBe('Full')
    expect(cards[3].value).toBe('Clear')
    expect(cards[4].value).toBe('Calm')
  })

  it('statusTone maps statuses to distinct tones', () => {
    expect(statusTone('green').dot).toContain('success')
    expect(statusTone('amber').dot).toContain('warning')
    expect(statusTone('red').dot).toContain('destructive')
  })
})

describe('plan lock derivation', () => {
  it('is unlocked with no status yet', () => {
    expect(planLock(undefined)).toEqual({ locked: false, reasons: [], overridden: false })
  })

  it('collects red-read reasons when locked', () => {
    const status = makeStatus({
      ladder: {
        drawdownPct: 16,
        rung: 'paused',
        nextRungPct: null,
        locked: true,
        status: 'red',
        detail: 'Paused: 16% drawdown.',
      },
    })
    status.newPlanUnlocked = false
    status.allGreen = false

    const lock = planLock(status)
    expect(lock.locked).toBe(true)
    expect(lock.reasons).toEqual(['Ladder: Paused: 16% drawdown.'])
  })

  it('reports override as unlocked', () => {
    const status = makeStatus()
    status.newPlanUnlocked = true
    status.activeOverride = { id: 'o1', kind: 'cockpitRed', reason: 'x', at: '' }
    const lock = planLock(status)
    expect(lock.locked).toBe(false)
    expect(lock.overridden).toBe(true)
  })
})

describe('override flow reducer', () => {
  it('walks the open → reason → submit → success flow', () => {
    let state = overrideFlowReducer(initialOverrideFlow, { type: 'open' })
    expect(state.dialogOpen).toBe(true)
    expect(canSubmitOverride(state)).toBe(false)

    state = overrideFlowReducer(state, { type: 'setReason', reason: 'A+ setup, planned yesterday' })
    expect(canSubmitOverride(state)).toBe(true)

    state = overrideFlowReducer(state, { type: 'submit' })
    expect(state.submitting).toBe(true)
    // No double-submits while in flight.
    expect(canSubmitOverride(state)).toBe(false)

    state = overrideFlowReducer(state, { type: 'succeeded' })
    expect(state).toEqual(initialOverrideFlow)
  })

  it('requires a meaningful reason', () => {
    let state = overrideFlowReducer(initialOverrideFlow, { type: 'open' })
    state = overrideFlowReducer(state, { type: 'setReason', reason: '  a ' })
    expect(canSubmitOverride(state)).toBe(false)
    // submit is a no-op when invalid
    expect(overrideFlowReducer(state, { type: 'submit' }).submitting).toBe(false)
  })

  it('keeps the dialog open with the error on failure', () => {
    let state = overrideFlowReducer(initialOverrideFlow, { type: 'open' })
    state = overrideFlowReducer(state, { type: 'setReason', reason: 'good reason' })
    state = overrideFlowReducer(state, { type: 'submit' })
    state = overrideFlowReducer(state, { type: 'failed', error: 'boom' })
    expect(state.dialogOpen).toBe(true)
    expect(state.submitting).toBe(false)
    expect(state.error).toBe('boom')
  })

  it('cannot cancel while submitting', () => {
    let state = overrideFlowReducer(initialOverrideFlow, { type: 'open' })
    state = overrideFlowReducer(state, { type: 'setReason', reason: 'good reason' })
    state = overrideFlowReducer(state, { type: 'submit' })
    expect(overrideFlowReducer(state, { type: 'cancel' }).dialogOpen).toBe(true)
  })
})
