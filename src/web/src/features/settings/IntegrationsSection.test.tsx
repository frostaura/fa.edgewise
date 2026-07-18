import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Provider } from 'react-redux'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { makeStore } from '@/app/store'
import {
  IntegrationsSection,
  backfillProgress,
  isValidWalletAddress,
  maskKeyLastFour,
  validateConnectForm,
} from '@/features/settings/IntegrationsSection'

describe('masked display logic', () => {
  it('masks a stored key down to its last four characters', () => {
    expect(maskKeyLastFour('X9z2')).toBe('••••X9z2')
    expect(maskKeyLastFour(undefined)).toBeNull()
    expect(maskKeyLastFour('')).toBeNull()
  })

  it('validates wallet addresses strictly', () => {
    expect(isValidWalletAddress('0x56687bf447db6ffa42ffe2204a05edaa20f55839')).toBe(true)
    expect(isValidWalletAddress(' 0x56687bf447db6ffa42ffe2204a05edaa20f55839 ')).toBe(true)
    expect(isValidWalletAddress('0x1234')).toBe(false)
    expect(isValidWalletAddress('56687bf447db6ffa42ffe2204a05edaa20f55839')).toBe(false)
    expect(isValidWalletAddress('0xZZ687bf447db6ffa42ffe2204a05edaa20f55839')).toBe(false)
  })

  it('computes binance backfill progress from sync stats', () => {
    expect(backfillProgress(undefined)).toBeNull()
    expect(backfillProgress({})).toBeNull()
    expect(backfillProgress({ symbolsTotal: 4, symbolsDone: 1, currentSymbol: 'BTCUSDT' })).toEqual({
      done: 1,
      total: 4,
      label: '1/4 symbols — BTCUSDT',
    })
  })
})

describe('connect form validation', () => {
  it('requires a bucket for every venue', () => {
    expect(validateConnectForm({ venue: 'polymarket', bucketId: '' })).toMatch(/bucket/i)
  })

  it('validates polymarket wallets', () => {
    expect(validateConnectForm({ venue: 'polymarket', bucketId: 'b1' })).toMatch(/wallet/i)
    expect(
      validateConnectForm({ venue: 'polymarket', bucketId: 'b1', walletAddress: 'nope' }),
    ).toMatch(/0x/)
    expect(
      validateConnectForm({
        venue: 'polymarket',
        bucketId: 'b1',
        walletAddress: '0x56687bf447db6ffa42ffe2204a05edaa20f55839',
      }),
    ).toBeNull()
  })

  it('requires both binance key and secret', () => {
    expect(validateConnectForm({ venue: 'binance', bucketId: 'b1', apiKey: 'k' })).toMatch(/secret/i)
    expect(
      validateConnectForm({ venue: 'binance', bucketId: 'b1', apiKey: 'k', apiSecret: 's' }),
    ).toBeNull()
  })

  it('requires a coinbase key name and a PEM with BEGIN/END lines', () => {
    expect(validateConnectForm({ venue: 'coinbase', bucketId: 'b1' })).toMatch(/key name/i)
    expect(
      validateConnectForm({ venue: 'coinbase', bucketId: 'b1', keyName: 'org/x', privateKeyPem: 'abc' }),
    ).toMatch(/PEM/i)
    expect(
      validateConnectForm({
        venue: 'coinbase',
        bucketId: 'b1',
        keyName: 'org/x',
        privateKeyPem: '-----BEGIN EC PRIVATE KEY-----\nxyz\n-----END EC PRIVATE KEY-----',
      }),
    ).toBeNull()
  })
})

describe('IntegrationsSection', () => {
  beforeEach(() => {
    vi.spyOn(window, 'fetch').mockResolvedValue(
      new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )
  })

  function renderSection() {
    return render(
      <Provider store={makeStore()}>
        <MemoryRouter>
          <IntegrationsSection />
        </MemoryRouter>
      </Provider>,
    )
  }

  it('shows a validation error before any request when the wallet is malformed', async () => {
    const user = userEvent.setup()
    renderSection()

    await user.type(screen.getByLabelText(/wallet address/i), 'not-a-wallet')
    const polymarketCard = screen.getByText('Polymarket').closest('[data-slot="card"]') ?? document.body
    const connect = Array.from(polymarketCard.querySelectorAll('button')).find(
      (b) => b.textContent === 'Connect',
    )!
    await user.click(connect)

    // Bucket is empty → the bucket error wins first.
    expect(await screen.findByRole('alert')).toHaveTextContent(/bucket/i)
  })

  it('renders the venue connect cards, CSV fallback and security note', () => {
    renderSection()

    expect(screen.getByText('Polymarket')).toBeInTheDocument()
    expect(screen.getByText('Binance')).toBeInTheDocument()
    expect(screen.getByText('Coinbase')).toBeInTheDocument()
    expect(screen.getByText(/read-only key required/i)).toBeInTheDocument()
    expect(screen.getByText(/proxy \/ deposit wallet/i)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /csv import/i })).toHaveAttribute(
      'href',
      '/journal/import',
    )
    expect(screen.getByText(/envelope-encrypted/i)).toBeInTheDocument()
  })
})
