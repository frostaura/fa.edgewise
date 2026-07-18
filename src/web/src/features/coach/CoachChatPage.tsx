import { useRef, useState } from 'react'
import { Link } from 'react-router'
import { BotIcon, LoaderIcon, SendIcon, WifiOffIcon } from 'lucide-react'

import { useAppSelector } from '@/app/hooks'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { streamCoachChat } from '@/features/coach/sse'
import type { ChatHistoryMessage } from '@/features/coach/types'
import { cn } from '@/lib/utils'

interface ChatBubble {
  role: 'user' | 'assistant'
  text: string
  tools: string[]
  pending?: boolean
}

const starterQuestions = [
  "What's my expectancy on breakout trades?",
  'When do I break my rules most?',
  'How calibrated are my forecasts?',
  'Do I trade worse right after a loss?',
]

const toolLabels: Record<string, string> = {
  get_expectancy: 'checking your expectancy…',
  get_trades: 'reading your trades…',
  get_trade: 'reading a trade…',
  get_adherence_trend: 'checking your adherence trend…',
  get_r_distribution: 'building your R distribution…',
  get_tilt: 'checking your tilt signature…',
  get_calibration: 'scoring your calibration…',
  get_ptr: 'checking your plan-then-trade ratio…',
}

export default function CoachChatPage() {
  const accessToken = useAppSelector((state) => state.auth.accessToken)
  const [messages, setMessages] = useState<ChatBubble[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [activeTool, setActiveTool] = useState<string | null>(null)
  const [offline, setOffline] = useState<string | null>(null)
  const historyRef = useRef<ChatHistoryMessage[]>([])

  const send = async (text: string) => {
    const message = text.trim()
    if (!message || busy) return

    setBusy(true)
    setInput('')
    setMessages((prev) => [
      ...prev,
      { role: 'user', text: message, tools: [] },
      { role: 'assistant', text: '', tools: [], pending: true },
    ])

    const history = [...historyRef.current]
    let answer = ''
    const tools: string[] = []

    const patchAssistant = (patch: Partial<ChatBubble>) =>
      setMessages((prev) => {
        const next = [...prev]
        next[next.length - 1] = { ...next[next.length - 1], ...patch }
        return next
      })

    try {
      for await (const event of streamCoachChat(message, history, accessToken)) {
        switch (event.type) {
          case 'tool':
            if (!tools.includes(event.name)) tools.push(event.name)
            setActiveTool(event.name)
            patchAssistant({ tools: [...tools] })
            break
          case 'delta':
            setActiveTool(null)
            answer += event.text
            patchAssistant({ text: answer })
            break
          case 'done':
            patchAssistant({ pending: false })
            historyRef.current = [
              ...history,
              { role: 'user', text: message },
              { role: 'assistant', text: answer },
            ]
            break
          case 'offline':
            setOffline(event.reason ?? 'offline')
            setMessages((prev) => prev.slice(0, -1))
            break
        }
      }
    } catch {
      patchAssistant({
        pending: false,
        text: answer || 'Something went wrong talking to the coach — try again.',
      })
    } finally {
      setActiveTool(null)
      setBusy(false)
    }
  }

  if (offline) {
    return (
      <div className="flex flex-col gap-6">
        <PageHeader
          title="Coach"
          description="An AI coach that knows your process — and holds you to it."
        />
        <EmptyState
          icon={WifiOffIcon}
          title="Coach is offline"
          hint={
            offline === 'opted_out'
              ? 'You have opted out of LLM features. Your deterministic analytics still run — flip the switch in Settings → LLM to bring the coach back.'
              : offline === 'over_budget'
                ? 'This month’s token budget is used up. Deterministic analytics keep working; the coach returns next month or after a budget raise in Settings → LLM.'
                : 'No LLM is configured on this server. All deterministic analytics still work.'
          }
          action={
            <Button asChild size="sm" variant="outline">
              <Link to="/journal">Open journal analytics</Link>
            </Button>
          }
        />
      </div>
    )
  }

  return (
    <div className="flex h-full min-h-[60vh] flex-col gap-4">
      <PageHeader
        title="Coach"
        description="Ask about your own journal — every answer cites the data it used."
      />

      <div className="flex flex-1 flex-col gap-3 overflow-y-auto rounded-xl border bg-surface/50 p-4">
        {messages.length === 0 && (
          <div className="m-auto flex max-w-md flex-col items-center gap-4 text-center">
            <BotIcon className="size-10 text-muted-foreground" aria-hidden />
            <p className="text-sm text-muted-foreground">
              The coach answers from your trades, plans and stats — it never gives trade calls.
            </p>
            <div className="flex flex-wrap justify-center gap-2">
              {starterQuestions.map((question) => (
                <button
                  key={question}
                  type="button"
                  onClick={() => void send(question)}
                  className="rounded-full border bg-background px-3 py-1.5 text-xs transition-colors hover:bg-muted"
                >
                  {question}
                </button>
              ))}
            </div>
          </div>
        )}

        {messages.map((bubble, i) => (
          <div
            key={i}
            className={cn('flex flex-col gap-1', bubble.role === 'user' ? 'items-end' : 'items-start')}
          >
            <div
              className={cn(
                'max-w-[85%] rounded-2xl px-3.5 py-2 text-sm whitespace-pre-wrap',
                bubble.role === 'user'
                  ? 'bg-primary text-primary-foreground'
                  : 'border bg-background',
              )}
            >
              {bubble.text ||
                (bubble.pending && (
                  <span className="flex items-center gap-2 text-muted-foreground">
                    <LoaderIcon className="size-3.5 animate-spin" aria-hidden />
                    {activeTool ? (toolLabels[activeTool] ?? `${activeTool}…`) : 'thinking…'}
                  </span>
                ))}
            </div>
            {bubble.role === 'assistant' && bubble.tools.length > 0 && (
              <div className="flex flex-wrap gap-1" aria-label="Data used">
                {bubble.tools.map((tool) => (
                  <Badge key={tool} variant="outline" className="text-[10px]">
                    {tool.replace(/^get_/, '').replaceAll('_', ' ')}
                  </Badge>
                ))}
              </div>
            )}
          </div>
        ))}
      </div>

      <form
        className="flex items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          void send(input)
        }}
      >
        <Input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask about your trading…"
          disabled={busy}
          aria-label="Message the coach"
        />
        <Button type="submit" size="icon" disabled={busy || !input.trim()} aria-label="Send">
          <SendIcon className="size-4" aria-hidden />
        </Button>
      </form>
    </div>
  )
}
