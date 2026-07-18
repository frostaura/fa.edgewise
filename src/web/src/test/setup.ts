import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(() => {
  cleanup()
})

/* Node's undici Request rejects relative URLs ("/api/…"), which is all the
 * app ever uses. Resolve them against the jsdom origin so fetchBaseQuery
 * works under test exactly like in the browser. */
const OriginalRequest = globalThis.Request
globalThis.Request = class extends OriginalRequest {
  constructor(input: RequestInfo | URL, init?: RequestInit) {
    if (typeof input === 'string' && input.startsWith('/')) {
      input = new URL(input, window.location.origin).toString()
    }
    super(input, init)
  }
} as typeof Request

/* jsdom lacks matchMedia — provide a light-scheme stub. */
if (typeof window !== 'undefined' && !window.matchMedia) {
  window.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }) as MediaQueryList
}

/* jsdom lacks ResizeObserver (used by the chart wrappers). */
if (typeof window !== 'undefined' && !window.ResizeObserver) {
  window.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver
}
