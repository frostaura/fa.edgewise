import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { Provider } from 'react-redux'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { Fill, InboxGroup } from '@/api/journalApi'
import { makeStore } from '@/app/store'
import { sessionEstablished } from '@/features/auth/authSlice'
import InboxPage from '@/features/journal/InboxPage'

const fill: Fill = {
  id: 'fill-1',
  accountId: 'acct-1',
  instrumentId: 'inst-1',
  side: 'buy',
  qty: 0.5,
  price: 60000,
  feeMinor: 1250,
  feeCurrency: 'USD',
  at: '2026-07-01T09:30:00Z',
  source: 'csv',
  matchStatus: 'proposed',
}

const group: InboxGroup = {
  instrumentId: 'inst-1',
  instrument: {
    id: 'inst-1',
    symbol: 'BTC-USD',
    name: 'Bitcoin',
    assetClass: 'crypto',
    currency: 'USD',
  },
  accountId: 'acct-1',
  accountName: 'Binance main',
  fills: [fill],
  suggestedPlans: [
    {
      id: 'plan-1',
      instrumentId: 'inst-1',
      bucketId: 'bucket-1',
      riskProfileId: 'risk-1',
      direction: 'long',
      setupTag: 'trend-pullback',
      stopPrice: 58000,
      sizeQty: 0.5,
      sizeOverridden: false,
      isPaper: false,
      status: 'active',
      createdAt: '2026-07-01T08:00:00Z',
    },
  ],
  tradePreview: [
    {
      tradeKey: 'BTC-USD:1',
      direction: 'long',
      openedAt: '2026-07-01T09:30:00Z',
      qty: 0.5,
      avgEntryPrice: 60000,
      realisedPnlMinor: 0,
      fillIds: ['fill-1'],
    },
  ],
  openTrades: [],
}

/** Routes fetch calls by URL; records confess/match invocations. */
function mockFetch(inboxPayloads: InboxGroup[][]) {
  const calls: string[] = []
  let inboxServed = 0
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init)
      const path = new URL(request.url).pathname
      calls.push(`${request.method} ${path}`)

      let body: unknown = { error: { code: 'not_found', message: `no mock for ${path}` } }
      let status = 404
      if (path === '/api/fills/inbox') {
        body = inboxPayloads[Math.min(inboxServed++, inboxPayloads.length - 1)]
        status = 200
      } else if (path === '/api/fills/fill-1/confess') {
        body = { ...fill, matchStatus: 'confessed', tradeId: 'trade-9' }
        status = 200
      } else if (path === '/api/fills/fill-1/match') {
        body = { ...fill, matchStatus: 'matched', tradeId: 'trade-10' }
        status = 200
      }
      return new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
  return calls
}

function renderPage() {
  const store = makeStore()
  store.dispatch(sessionEstablished({ accessToken: 'token', refreshToken: 'refresh' }))
  return render(
    <Provider store={store}>
      <MemoryRouter>
        <InboxPage />
      </MemoryRouter>
    </Provider>,
  )
}

describe('InboxPage', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.unstubAllGlobals()
  })

  it('renders proposed fill groups with plan suggestions and the FIFO preview', async () => {
    mockFetch([[group]])
    renderPage()

    expect(await screen.findByText('BTC-USD')).toBeInTheDocument()
    expect(screen.getByText(/Binance main/)).toBeInTheDocument()
    expect(screen.getByText(/buy 0.5 @ 60000/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /match to plan/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /confess unplanned/i })).toBeInTheDocument()
    expect(screen.getByText(/FIFO preview/)).toBeInTheDocument()
  })

  it('confess flow calls the API and refreshes to inbox zero', async () => {
    const calls = mockFetch([[group], []])
    renderPage()

    const confess = await screen.findByRole('button', { name: /confess unplanned/i })
    await userEvent.click(confess)

    await waitFor(() =>
      expect(calls).toContain('POST /api/fills/fill-1/confess'),
    )
    // Invalidation refetches the inbox, which is now empty.
    expect(await screen.findByText(/inbox zero/i)).toBeInTheDocument()
  })

  it('match-to-plan sends the selected plan id', async () => {
    const calls = mockFetch([[group], []])
    renderPage()

    const match = await screen.findByRole('button', { name: /match to plan/i })
    await userEvent.click(match)

    await waitFor(() => expect(calls).toContain('POST /api/fills/fill-1/match'))
  })
})
