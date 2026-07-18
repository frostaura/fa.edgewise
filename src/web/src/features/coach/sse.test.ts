import { describe, expect, it } from 'vitest'

import { parseFrame, parseSseStream } from '@/features/coach/sse'
import type { CoachChatEvent } from '@/features/coach/types'

function streamOf(chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder()
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
      controller.close()
    },
  })
}

async function collect(stream: ReadableStream<Uint8Array>): Promise<CoachChatEvent[]> {
  const events: CoachChatEvent[] = []
  for await (const event of parseSseStream(stream)) events.push(event)
  return events
}

describe('parseFrame', () => {
  it('parses a data frame', () => {
    expect(parseFrame('data: {"type":"delta","text":"hi"}')).toEqual({ type: 'delta', text: 'hi' })
  })

  it('ignores comments and malformed frames', () => {
    expect(parseFrame(': heartbeat')).toBeNull()
    expect(parseFrame('data: {not json')).toBeNull()
    expect(parseFrame('')).toBeNull()
  })
})

describe('parseSseStream', () => {
  it('parses multiple events from one chunk', async () => {
    const events = await collect(
      streamOf([
        'data: {"type":"tool","name":"get_expectancy"}\n\ndata: {"type":"delta","text":"Your"}\n\n',
      ]),
    )
    expect(events).toEqual([
      { type: 'tool', name: 'get_expectancy' },
      { type: 'delta', text: 'Your' },
    ])
  })

  it('reassembles frames split across chunks', async () => {
    const events = await collect(
      streamOf(['data: {"type":"del', 'ta","text":"expectancy is 0.4R"}', '\n\n']),
    )
    expect(events).toEqual([{ type: 'delta', text: 'expectancy is 0.4R' }])
  })

  it('parses the full protocol sequence ending in done', async () => {
    const events = await collect(
      streamOf([
        'data: {"type":"tool","name":"get_tilt"}\n\n',
        'data: {"type":"delta","text":"After a loss "}\n\n',
        'data: {"type":"delta","text":"you drop 0.5R."}\n\n',
        'data: {"type":"done","insightId":"abc-123"}\n\n',
      ]),
    )
    expect(events).toHaveLength(4)
    expect(events[3]).toEqual({ type: 'done', insightId: 'abc-123' })
  })

  it('parses the offline event', async () => {
    const events = await collect(streamOf(['data: {"type":"offline","reason":"no_api_key"}\n\n']))
    expect(events).toEqual([{ type: 'offline', reason: 'no_api_key' }])
  })

  it('flushes a trailing frame without a final blank line', async () => {
    const events = await collect(streamOf(['data: {"type":"delta","text":"tail"}']))
    expect(events).toEqual([{ type: 'delta', text: 'tail' }])
  })
})
