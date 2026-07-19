import type { AccountSyncStats, ConnectAccountRequest } from '@/api/accountsApi'

/** "1234" → "••••1234"; nothing stored → null. */
export function maskKeyLastFour(keyLastFour?: string): string | null {
  return keyLastFour ? `••••${keyLastFour}` : null
}

export function isValidWalletAddress(address: string): boolean {
  return /^0x[0-9a-fA-F]{40}$/.test(address.trim())
}

/** Returns a user-facing validation error for a connect form, or null when submittable. */
export function validateConnectForm(request: {
  venue: ConnectAccountRequest['venue']
  bucketId: string
  walletAddress?: string
  apiKey?: string
  apiSecret?: string
  keyName?: string
  privateKeyPem?: string
}): string | null {
  if (!request.bucketId) return 'Pick the bucket this account belongs to.'
  switch (request.venue) {
    case 'polymarket':
      if (!request.walletAddress?.trim()) return 'Enter your Polymarket wallet address.'
      if (!isValidWalletAddress(request.walletAddress))
        return 'That does not look like a wallet address (0x followed by 40 hex characters).'
      return null
    case 'binance':
      if (!request.apiKey?.trim() || !request.apiSecret?.trim())
        return 'Both the API key and the API secret are required.'
      return null
    case 'coinbase':
      if (!request.keyName?.trim()) return 'Enter the CDP API key name.'
      if (!request.privateKeyPem?.includes('BEGIN'))
        return 'Paste the full private key PEM, including the BEGIN/END lines.'
      return null
  }
}

/** Binance backfill progress out of lastSyncStats, or null when not applicable. */
export function backfillProgress(
  stats?: AccountSyncStats,
): { done: number; total: number; label: string } | null {
  if (!stats || stats.symbolsTotal == null || stats.symbolsTotal <= 0) return null
  const done = Math.min(stats.symbolsDone ?? 0, stats.symbolsTotal)
  const current = stats.currentSymbol ? ` — ${stats.currentSymbol}` : ''
  return { done, total: stats.symbolsTotal, label: `${done}/${stats.symbolsTotal} symbols${current}` }
}
