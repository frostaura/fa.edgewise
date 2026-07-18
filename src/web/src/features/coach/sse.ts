import type { ChatHistoryMessage, CoachChatEvent } from '@/features/coach/types'

/**
 * Parses an SSE byte stream ("data: {json}\n\n" frames) into coach chat events.
 * Tolerates frames split across chunks and multiple frames per chunk.
 */
export async function* parseSseStream(
  stream: ReadableStream<Uint8Array>,
): AsyncGenerator<CoachChatEvent> {
  const reader = stream.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })

      let boundary = buffer.indexOf('\n\n')
      while (boundary >= 0) {
        const frame = buffer.slice(0, boundary)
        buffer = buffer.slice(boundary + 2)
        const event = parseFrame(frame)
        if (event) yield event
        boundary = buffer.indexOf('\n\n')
      }
    }

    // Flush a trailing frame without the final blank line.
    const event = parseFrame(buffer)
    if (event) yield event
  } finally {
    reader.releaseLock()
  }
}

/** Parses one SSE frame; returns null for comments/heartbeats/garbage. */
export function parseFrame(frame: string): CoachChatEvent | null {
  const dataLines = frame
    .split('\n')
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice(5).trimStart())
  if (dataLines.length === 0) return null

  try {
    const parsed: unknown = JSON.parse(dataLines.join('\n'))
    if (parsed && typeof parsed === 'object' && 'type' in parsed) {
      return parsed as CoachChatEvent
    }
  } catch {
    // Malformed frame — skip rather than kill the stream.
  }
  return null
}

/**
 * POSTs a chat message to /api/coach/chat and yields the SSE events.
 * Auth uses the same Bearer token the RTK Query base query attaches.
 */
export async function* streamCoachChat(
  message: string,
  history: ChatHistoryMessage[],
  accessToken: string | null,
  signal?: AbortSignal,
): AsyncGenerator<CoachChatEvent> {
  const response = await fetch('/api/coach/chat', {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      ...(accessToken ? { authorization: `Bearer ${accessToken}` } : {}),
    },
    body: JSON.stringify({ message, history }),
    signal,
  })

  if (!response.ok || !response.body) {
    throw new Error(`coach chat failed (${response.status})`)
  }

  yield* parseSseStream(response.body)
}
