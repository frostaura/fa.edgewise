import * as React from 'react'
import { Link } from 'react-router'
import { ArrowLeftIcon, InboxIcon, Trash2Icon } from 'lucide-react'
import { toast } from 'sonner'

import {
  useConfessFillMutation,
  useDeleteFillMutation,
  useGetInboxQuery,
  useMatchFillMutation,
  type Fill,
  type InboxGroup,
} from '@/api/journalApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import { formatMinor } from '@/features/journal/sizing'

function formatDateTime(iso: string) {
  return new Date(iso).toLocaleString(undefined, {
    day: '2-digit',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function FillRow({ fill, onDelete }: { fill: Fill; onDelete?: () => void }) {
  return (
    <li className="flex items-center gap-3 py-1.5 text-sm">
      <span
        className={cn(
          'size-2 shrink-0 rounded-full',
          fill.side === 'buy' ? 'bg-success' : 'bg-destructive',
        )}
        aria-hidden
      />
      <span className="w-28 text-xs text-muted-foreground">{formatDateTime(fill.at)}</span>
      <span className="font-medium">
        {fill.side} {fill.qty} @ {fill.price}
      </span>
      <span className="text-xs text-muted-foreground">
        fee {formatMinor(fill.feeMinor, fill.feeCurrency)}
      </span>
      <Badge variant="outline" className="ml-auto">
        {fill.source}
      </Badge>
      {fill.source === 'manual' && onDelete && (
        <Button variant="ghost" size="icon" aria-label="Delete fill" onClick={onDelete}>
          <Trash2Icon className="size-4" />
        </Button>
      )}
    </li>
  )
}

/**
 * One inbox group = proposed fills for (instrument, account). Three-tap triage:
 * match to an active plan, attach to an open trade, or confess unplanned.
 */
function InboxGroupCard({ group }: { group: InboxGroup }) {
  const [matchFill, { isLoading: matching }] = useMatchFillMutation()
  const [confessFill, { isLoading: confessing }] = useConfessFillMutation()
  const [deleteFill] = useDeleteFillMutation()
  const [planId, setPlanId] = React.useState(group.suggestedPlans[0]?.id ?? '')
  const [tradeId, setTradeId] = React.useState(group.openTrades[0]?.id ?? '')

  const busy = matching || confessing
  const firstFill = group.fills[0]

  const run = async (action: () => Promise<unknown>, success: string) => {
    try {
      await action()
      toast.success(success)
    } catch (err) {
      toast.error(getApiErrorMessage(err))
    }
  }

  return (
    <Card className="gap-3 py-4">
      <CardHeader className="px-4">
        <CardTitle className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-base">{group.instrument?.symbol ?? 'Unknown instrument'}</span>
          <span className="font-normal text-muted-foreground">
            {group.accountName ?? 'manual'} · {group.fills.length} fill
            {group.fills.length === 1 ? '' : 's'}
          </span>
          {group.tradePreview.length > 0 && (
            <Badge variant="secondary" className="ml-auto">
              groups into {group.tradePreview.length} trade
              {group.tradePreview.length === 1 ? '' : 's'}
            </Badge>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 px-4">
        <ul className="flex flex-col divide-y">
          {group.fills.map((fill) => (
            <FillRow
              key={fill.id}
              fill={fill}
              onDelete={() =>
                void run(() => deleteFill(fill.id).unwrap(), 'Fill deleted')
              }
            />
          ))}
        </ul>

        {group.tradePreview.length > 0 && (
          <p className="text-xs text-muted-foreground">
            FIFO preview:{' '}
            {group.tradePreview
              .map(
                (t) =>
                  `${t.direction} ${t.qty} @ ${t.avgEntryPrice}${
                    t.closedAt ? ` → ${formatMinor(t.realisedPnlMinor)}` : ' (open)'
                  }`,
              )
              .join(' · ')}
          </p>
        )}

        <div className="flex flex-wrap items-center gap-2">
          {/* Tap 1: match to an active plan */}
          {group.suggestedPlans.length > 0 && (
            <span className="flex items-center gap-1">
              <Select value={planId} onValueChange={setPlanId}>
                <SelectTrigger className="h-8 text-xs">
                  <SelectValue placeholder="Active plan" />
                </SelectTrigger>
                <SelectContent>
                  {group.suggestedPlans.map((plan) => (
                    <SelectItem key={plan.id} value={plan.id}>
                      {plan.setupTag ?? 'plan'} · {plan.direction} · stop {plan.stopPrice}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button
                size="sm"
                disabled={busy || !planId || !firstFill}
                onClick={() =>
                  void run(
                    () =>
                      matchFill({ id: firstFill.id, planId, createTrade: true }).unwrap(),
                    'Matched to plan — trade created',
                  )
                }
              >
                Match to plan
              </Button>
            </span>
          )}

          {/* Tap 2: attach to an open trade */}
          {group.openTrades.length > 0 && (
            <span className="flex items-center gap-1">
              <Select value={tradeId} onValueChange={setTradeId}>
                <SelectTrigger className="h-8 text-xs">
                  <SelectValue placeholder="Open trade" />
                </SelectTrigger>
                <SelectContent>
                  {group.openTrades.map((trade) => (
                    <SelectItem key={trade.id} value={trade.id}>
                      {trade.direction} {trade.qty} @ {trade.avgEntryPrice}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button
                size="sm"
                variant="outline"
                disabled={busy || !tradeId || !firstFill}
                onClick={() =>
                  void run(
                    () =>
                      matchFill({ id: firstFill.id, tradeId, createTrade: false }).unwrap(),
                    'Attached to open trade',
                  )
                }
              >
                Attach to trade
              </Button>
            </span>
          )}

          {/* Tap 3: confess unplanned */}
          <Button
            size="sm"
            variant="secondary"
            disabled={busy || !firstFill}
            onClick={() =>
              void run(
                () => confessFill(firstFill.id).unwrap(),
                'Confessed as unplanned — honesty counts',
              )
            }
          >
            Confess unplanned
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">
          Actions apply to the oldest fill first; the group re-groups as fills are triaged.
        </p>
      </CardContent>
    </Card>
  )
}

export default function InboxPage() {
  const { data: groups, isLoading } = useGetInboxQuery()

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2 mb-2">
          <Link to="/journal">
            <ArrowLeftIcon aria-hidden />
            Journal
          </Link>
        </Button>
        <PageHeader
          title="Inbox"
          description="Unreviewed fills waiting to be matched to plans — or confessed."
        />
      </div>

      {isLoading ? (
        <div className="flex flex-col gap-3">
          <Skeleton className="h-40 w-full" />
          <Skeleton className="h-40 w-full" />
        </div>
      ) : !groups || groups.length === 0 ? (
        <EmptyState
          icon={InboxIcon}
          title="Inbox zero"
          hint="Fills from imports and manual logs land here until you match them to a plan or log them as unplanned. Nothing is waiting on you right now."
        />
      ) : (
        <div className="flex flex-col gap-4">
          {groups.map((group) => (
            <InboxGroupCard key={`${group.instrumentId}:${group.accountId}`} group={group} />
          ))}
        </div>
      )}
    </div>
  )
}
