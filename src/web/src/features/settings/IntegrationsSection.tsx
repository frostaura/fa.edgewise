import { useState } from 'react'
import { Link } from 'react-router'
import {
  BitcoinIcon,
  CoinsIcon,
  ExternalLinkIcon,
  FileSpreadsheetIcon,
  LockIcon,
  PercentCircleIcon,
  RefreshCwIcon,
  type LucideIcon,
} from 'lucide-react'

import {
  useConnectAccountMutation,
  useGetAccountsQuery,
  useGetBucketSummariesQuery,
  useRevokeAccountMutation,
  useSyncAccountMutation,
  type AccountSyncStats,
  type ConnectAccountRequest,
  type IntegrationAccount,
} from '@/api/accountsApi'
import { getApiErrorMessage } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import {
  backfillProgress,
  maskKeyLastFour,
  validateConnectForm,
} from '@/features/settings/integrationsLogic'

const statusStyles: Record<IntegrationAccount['status'], { dot: string; label: string }> = {
  connected: { dot: 'bg-success', label: 'Connected' },
  error: { dot: 'bg-destructive', label: 'Error' },
  revoked: { dot: 'bg-muted-foreground', label: 'Revoked' },
}

const venueMeta: Partial<Record<IntegrationAccount['venue'], { label: string; icon: LucideIcon }>> = {
  polymarket: { label: 'Polymarket', icon: PercentCircleIcon },
  binance: { label: 'Binance', icon: CoinsIcon },
  coinbase: { label: 'Coinbase', icon: BitcoinIcon },
}

function formatWhen(iso?: string): string {
  return iso ? new Date(iso).toLocaleString() : 'never'
}

// ----------------------------------------------------------- account row

function AccountRow({ account }: { account: IntegrationAccount }) {
  const [syncAccount, syncState] = useSyncAccountMutation()
  const [revokeAccount, revokeState] = useRevokeAccountMutation()
  const stats = account.lastSyncStats
  const status = statusStyles[account.status]
  const meta = venueMeta[account.venue]
  const Icon = meta?.icon ?? CoinsIcon
  const progress = backfillProgress(stats)
  const syncing = !!stats?.syncing || syncState.isLoading

  return (
    <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-4">
      <div className="flex flex-wrap items-center gap-3">
        <Icon className="size-5 text-muted-foreground" aria-hidden />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="truncate font-medium">{account.name}</span>
            <Badge variant="outline">{meta?.label ?? account.venue}</Badge>
          </div>
          <div className="text-xs text-muted-foreground">
            {account.walletAddress ?? maskKeyLastFour(account.keyLastFour) ?? 'no credentials stored'}
          </div>
        </div>
        <span className="flex items-center gap-1.5 text-sm" data-testid={`status-${account.id}`}>
          <span className={cn('size-2 rounded-full', status.dot)} aria-hidden />
          {status.label}
        </span>
        {account.status !== 'revoked' && (
          <div className="flex gap-2">
            <Button
              size="sm"
              variant="outline"
              disabled={syncing}
              onClick={() => void syncAccount(account.id)}
            >
              <RefreshCwIcon className={cn(syncing && 'animate-spin')} />
              {syncing ? 'Syncing…' : 'Sync now'}
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="text-destructive hover:text-destructive"
              disabled={revokeState.isLoading}
              onClick={() => {
                if (window.confirm(`Revoke ${account.name}? Stored credentials are deleted immediately.`)) {
                  void revokeAccount(account.id)
                }
              }}
            >
              Revoke
            </Button>
          </div>
        )}
      </div>

      <div className="text-xs text-muted-foreground">
        Last sync {formatWhen(account.lastSyncAt)}
        {stats?.lastRunFills != null && ` · ${stats.lastRunFills} fills imported last run`}
        {stats?.totalFills != null && ` · ${stats.totalFills} total`}
      </div>

      {progress && (syncing || progress.done < progress.total) && (
        <div className="flex flex-col gap-1" data-testid="backfill-progress">
          <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted">
            <div
              className="h-full rounded-full bg-primary transition-all"
              style={{ width: `${progress.total ? Math.round((progress.done / progress.total) * 100) : 0}%` }}
            />
          </div>
          <span className="text-xs text-muted-foreground">Backfill: {progress.label}</span>
        </div>
      )}

      {stats?.error && account.status === 'error' && (
        <p className="rounded-md bg-destructive/10 px-3 py-2 text-sm text-destructive">{stats.error}</p>
      )}
      {stats?.warning && (
        <p className="rounded-md bg-warning/10 px-3 py-2 text-sm text-warning">{stats.warning}</p>
      )}
    </div>
  )
}

// -------------------------------------------------------- connect forms

function BucketSelect({
  value,
  onChange,
  id,
}: {
  value: string
  onChange: (value: string) => void
  id: string
}) {
  const { data: buckets, isError } = useGetBucketSummariesQuery()
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>Bucket</Label>
      <select
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="h-9 rounded-md border border-border bg-surface px-3 text-sm"
      >
        <option value="">{isError ? 'Could not load buckets' : 'Select a bucket…'}</option>
        {(buckets ?? []).map((bucket) => (
          <option key={bucket.id} value={bucket.id}>
            {bucket.name}
          </option>
        ))}
      </select>
    </div>
  )
}

function ConnectCard({
  title,
  description,
  venue,
  children,
  buildRequest,
  helper,
}: {
  title: string
  description: string
  venue: ConnectAccountRequest['venue']
  children: (bucketId: string, setBucketId: (v: string) => void) => React.ReactNode
  buildRequest: (bucketId: string) => Omit<ConnectAccountRequest, 'venue' | 'bucketId'>
  helper?: React.ReactNode
}) {
  const [bucketId, setBucketId] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [warning, setWarning] = useState<string | null>(null)
  const [connect, connectState] = useConnectAccountMutation()

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setWarning(null)
    const request: ConnectAccountRequest = { venue, bucketId, ...buildRequest(bucketId) }
    const validationError = validateConnectForm(request)
    setFormError(validationError)
    if (validationError) return
    try {
      const result = await connect(request).unwrap()
      setWarning(result.warning ?? null)
    } catch (err) {
      setFormError(getApiErrorMessage(err, 'Could not connect the account.'))
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-3" onSubmit={(e) => void submit(e)} noValidate>
          {children(bucketId, setBucketId)}
          {helper}
          {formError && (
            <p role="alert" className="text-sm text-destructive">
              {formError}
            </p>
          )}
          {warning && (
            <p role="status" className="rounded-md bg-warning/10 px-3 py-2 text-sm text-warning">
              {warning}
            </p>
          )}
          {connectState.isSuccess && !warning && (
            <p role="status" className="text-sm text-success">
              Connected — use “Sync now” to import your history.
            </p>
          )}
          <Button type="submit" disabled={connectState.isLoading} className="self-start">
            {connectState.isLoading ? 'Validating…' : 'Connect'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

function PolymarketConnectCard() {
  const [wallet, setWallet] = useState('')
  const [name, setName] = useState('')
  return (
    <ConnectCard
      title="Polymarket"
      description="Read-only import of your prediction-market trades — no keys needed, just your wallet address."
      venue="polymarket"
      buildRequest={() => ({ walletAddress: wallet, name: name || undefined })}
      helper={
        <p className="text-xs text-muted-foreground">
          Use the <strong>proxy / deposit wallet</strong> shown in your Polymarket profile (the address your
          USDC sits in), not the wallet you sign in with. Trades are keyed to the proxy wallet.
        </p>
      }
    >
      {(bucketId, setBucketId) => (
        <>
          <BucketSelect id="pm-bucket" value={bucketId} onChange={setBucketId} />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="pm-wallet">Wallet address</Label>
            <Input
              id="pm-wallet"
              placeholder="0x…"
              value={wallet}
              onChange={(e) => setWallet(e.target.value)}
              autoComplete="off"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="pm-name">Display name (optional)</Label>
            <Input id="pm-name" value={name} onChange={(e) => setName(e.target.value)} />
          </div>
        </>
      )}
    </ConnectCard>
  )
}

function BinanceConnectCard() {
  const [apiKey, setApiKey] = useState('')
  const [apiSecret, setApiSecret] = useState('')
  return (
    <ConnectCard
      title="Binance"
      description="Spot trade history via a read-only API key."
      venue="binance"
      buildRequest={() => ({ apiKey, apiSecret })}
      helper={
        <p className="text-xs text-muted-foreground">
          <strong>Read-only key required</strong> — we reject keys with trade or withdrawal permissions.
          An IP allow-list on the key is optional but recommended.{' '}
          <a
            className="inline-flex items-center gap-0.5 text-primary underline-offset-2 hover:underline"
            href="https://www.binance.com/en/my/settings/api-management"
            target="_blank"
            rel="noreferrer"
          >
            Binance API management <ExternalLinkIcon className="size-3" />
          </a>
        </p>
      }
    >
      {(bucketId, setBucketId) => (
        <>
          <BucketSelect id="bn-bucket" value={bucketId} onChange={setBucketId} />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="bn-key">API key</Label>
            <Input id="bn-key" value={apiKey} onChange={(e) => setApiKey(e.target.value)} autoComplete="off" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="bn-secret">API secret</Label>
            <Input
              id="bn-secret"
              type="password"
              value={apiSecret}
              onChange={(e) => setApiSecret(e.target.value)}
              autoComplete="off"
            />
          </div>
        </>
      )}
    </ConnectCard>
  )
}

function CoinbaseConnectCard() {
  const [keyName, setKeyName] = useState('')
  const [pem, setPem] = useState('')
  return (
    <ConnectCard
      title="Coinbase"
      description="Advanced Trade fills via a CDP API key (ES256 JWT — validated before we store anything)."
      venue="coinbase"
      buildRequest={() => ({ keyName, privateKeyPem: pem })}
      helper={
        <p className="text-xs text-muted-foreground">
          Create a key in the{' '}
          <a
            className="inline-flex items-center gap-0.5 text-primary underline-offset-2 hover:underline"
            href="https://portal.cdp.coinbase.com/access/api"
            target="_blank"
            rel="noreferrer"
          >
            CDP portal <ExternalLinkIcon className="size-3" />
          </a>{' '}
          with only the <em>view</em> permission, then paste the key name and the EC private key PEM.
        </p>
      }
    >
      {(bucketId, setBucketId) => (
        <>
          <BucketSelect id="cb-bucket" value={bucketId} onChange={setBucketId} />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="cb-keyname">Key name</Label>
            <Input
              id="cb-keyname"
              placeholder="organizations/…/apiKeys/…"
              value={keyName}
              onChange={(e) => setKeyName(e.target.value)}
              autoComplete="off"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="cb-pem">Private key (PEM)</Label>
            <textarea
              id="cb-pem"
              value={pem}
              onChange={(e) => setPem(e.target.value)}
              rows={5}
              spellCheck={false}
              placeholder={'-----BEGIN EC PRIVATE KEY-----\n…\n-----END EC PRIVATE KEY-----'}
              className="rounded-md border border-border bg-surface px-3 py-2 font-mono text-xs"
            />
          </div>
        </>
      )}
    </ConnectCard>
  )
}

// ------------------------------------------------------------- section

export function IntegrationsSection() {
  const [polling, setPolling] = useState(false)
  const { data: accounts, isLoading } = useGetAccountsQuery(undefined, {
    pollingInterval: polling ? 2500 : 0,
  })

  const anySyncing = useMemo(
    () => (accounts ?? []).some((a) => a.lastSyncStats?.syncing),
    [accounts],
  )
  useEffect(() => setPolling(anySyncing), [anySyncing])

  const exchangeAccounts = (accounts ?? []).filter((a) =>
    ['polymarket', 'binance', 'coinbase'].includes(a.venue),
  )

  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-col gap-3">
        <h2 className="text-sm font-semibold text-muted-foreground">Connected accounts</h2>
        {isLoading && <p className="text-sm text-muted-foreground">Loading accounts…</p>}
        {!isLoading && exchangeAccounts.length === 0 && (
          <p className="text-sm text-muted-foreground">
            Nothing connected yet — add an exchange below and your fills will land in the journal inbox.
          </p>
        )}
        {exchangeAccounts.map((account) => (
          <AccountRow key={account.id} account={account} />
        ))}
      </section>

      <section className="grid gap-4 lg:grid-cols-2">
        <PolymarketConnectCard />
        <BinanceConnectCard />
        <CoinbaseConnectCard />

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <FileSpreadsheetIcon className="size-4" aria-hidden /> EasyEquities / other brokers
            </CardTitle>
            <CardDescription>
              EasyEquities and most SA brokers have no public API — export a statement and use CSV import
              instead.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button asChild variant="outline" size="sm">
              <Link to="/journal/import">Go to CSV import</Link>
            </Button>
          </CardContent>
        </Card>
      </section>

      <Card>
        <CardContent className="flex items-start gap-3 pt-6 text-sm text-muted-foreground">
          <LockIcon className="mt-0.5 size-4 shrink-0" aria-hidden />
          <p>
            API keys are envelope-encrypted (AES-256-GCM) before they touch the database, never appear in
            responses or logs, and never leave the server. Revoking an account deletes its stored
            credentials immediately. All exchange keys are read-only by policy — we refuse keys that can
            trade or withdraw.
          </p>
        </CardContent>
      </Card>
    </div>
  )
}
