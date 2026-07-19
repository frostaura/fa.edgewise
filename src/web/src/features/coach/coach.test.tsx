import { Provider } from 'react-redux'
import { MemoryRouter } from 'react-router'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { makeStore } from '@/app/store'
import { InsightCard } from '@/components/domain/InsightCard'
import CoachChatPage from '@/features/coach/CoachChatPage'
import type { Insight } from '@/features/coach/types'

function renderWithProviders(ui: React.ReactElement) {
  return render(
    <Provider store={makeStore()}>
      <MemoryRouter>{ui}</MemoryRouter>
    </Provider>,
  )
}

const insight: Insight = {
  id: '11111111-1111-1111-1111-111111111111',
  type: 'postMortem',
  tradeId: '22222222-2222-2222-2222-222222222222',
  content: {
    observations: [
      { text: 'Closed at -1.0R with grade B adherence', citations: ['trade:22222222-2222-2222-2222-222222222222'] },
    ],
    deviations: [{ text: 'Early exit: left before the rule fired', citations: ['adherence:x'] }],
    riskFlags: [{ text: 'Size ran 2× the profile', citations: ['stat:risk'] }],
    patternLinks: [{ text: 'Third early exit in 30 days', citations: ['stat:history_digest'] }],
    question: { text: 'What did you feel at the exit?', citations: ['trade:x'] },
    kudos: { text: 'Stop stayed where the plan put it', citations: ['adherence:x'] },
  },
  citations: [
    { ref: 'trade:22222222-2222-2222-2222-222222222222', label: 'BTC trade 12 Jul' },
    { ref: 'stat:history_digest', label: '30-day pattern' },
  ],
  modelTag: 'deterministic',
  promptVersion: 'v1',
  feedback: 'none',
  createdAt: new Date().toISOString(),
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('InsightCard', () => {
  it('renders every content section with its heading', () => {
    renderWithProviders(<InsightCard insight={insight} />)

    expect(screen.getByText('Observations')).toBeInTheDocument()
    expect(screen.getByText('Closed at -1.0R with grade B adherence')).toBeInTheDocument()
    expect(screen.getByText('Deviations')).toBeInTheDocument()
    expect(screen.getByText('Risk flags')).toBeInTheDocument()
    expect(screen.getByText('Patterns')).toBeInTheDocument()
    expect(screen.getByText('Question for you')).toBeInTheDocument()
    expect(screen.getByText('Kudos')).toBeInTheDocument()
  })

  it('shows humanised provenance chips and the model tag footer', () => {
    renderWithProviders(<InsightCard insight={insight} />)

    expect(screen.getByText('trade #2222')).toBeInTheDocument()
    expect(screen.getByText('30-day pattern')).toBeInTheDocument()
    expect(screen.getByText('rules-based')).toBeInTheDocument()
  })

  it('exposes feedback buttons and a reason popover on thumbs down', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InsightCard insight={insight} />)

    expect(screen.getByRole('button', { name: 'Helpful' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Not helpful' }))
    expect(await screen.findByText('What was off?')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send feedback' })).toBeInTheDocument()
  })

  it('shows the regenerate action only when a handler is provided', async () => {
    const onRegenerate = vi.fn()
    const user = userEvent.setup()
    const { rerender } = renderWithProviders(
      <InsightCard insight={insight} onRegenerate={onRegenerate} />,
    )

    await user.click(screen.getByRole('button', { name: 'Regenerate' }))
    expect(onRegenerate).toHaveBeenCalledOnce()

    rerender(
      <Provider store={makeStore()}>
        <MemoryRouter>
          <InsightCard insight={insight} />
        </MemoryRouter>
      </Provider>,
    )
    expect(screen.queryByRole('button', { name: 'Regenerate' })).not.toBeInTheDocument()
  })
})

describe('CoachChatPage offline state', () => {
  function sseResponse(body: string) {
    const encoder = new TextEncoder()
    return {
      ok: true,
      status: 200,
      body: new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(encoder.encode(body))
          controller.close()
        },
      }),
    } as Response
  }

  it('shows the offline card when the server reports no LLM', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(sseResponse('data: {"type":"offline","reason":"no_api_key"}\n\n')),
    )
    const user = userEvent.setup()
    renderWithProviders(<CoachChatPage />)

    // Starter questions are offered before any conversation.
    const starter = screen.getByRole('button', { name: "What's my expectancy on breakout trades?" })
    await user.click(starter)

    await waitFor(() => expect(screen.getByText('Coach is offline')).toBeInTheDocument())
    expect(screen.getByText(/No LLM is configured/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open journal analytics' })).toBeInTheDocument()
  })

  it('streams deltas into an assistant bubble and shows tool chips', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        sseResponse(
          'data: {"type":"tool","name":"get_expectancy"}\n\n' +
            'data: {"type":"delta","text":"Breakouts run 0.4R."}\n\n' +
            'data: {"type":"done","insightId":"i1"}\n\n',
        ),
      ),
    )
    const user = userEvent.setup()
    renderWithProviders(<CoachChatPage />)

    await user.type(screen.getByLabelText('Message the coach'), 'expectancy?')
    await user.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() => expect(screen.getByText('Breakouts run 0.4R.')).toBeInTheDocument())
    expect(screen.getByText('expectancy')).toBeInTheDocument() // tool chip
  })
})
