import * as React from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import {
  ChevronLeftIcon,
  ChevronRightIcon,
  InboxIcon,
  NotebookPenIcon,
  PlusIcon,
  SearchIcon,
  ZapIcon,
} from 'lucide-react'

import {
  useGetInboxCountQuery,
  useGetTradesQuery,
  type EmotionTag,
  type TradeListItem,
  type TradeStatus,
} from '@/api/journalApi'
import { AdherenceChip } from '@/components/domain/AdherenceChip'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { RValue } from '@/components/domain/RValue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { AnalyticsSection } from '@/features/journal/AnalyticsSection'
import { QuickLogDialog } from '@/features/journal/QuickLogDialog'
import { formatMinor } from '@/features/journal/sizing'

const ALL = 'all'

function pnlTone(minor: number) {
  return minor > 0 ? 'text-success' : minor < 0 ? 'text-destructive' : 'text-muted-foreground'
}

function formatDate(iso: string) {
  return new Date(iso).toLocaleDateString(undefined, { day: '2-digit', month: 'short' })
}

function TradeRowMobile({ trade }: { trade: TradeListItem }) {
  return (
    <Link to={`/journal/trades/${trade.id}`} className="block">
      <Card className="gap-1.5 px-4 py-3">
        <div className="flex items-center justify-between gap-2">
          <span className="flex items-center gap-2 font-medium">
            {trade.instrumentSymbol}
            <Badge variant={trade.direction === 'long' ? 'success' : 'destructive'}>
              {trade.direction}
            </Badge>
            {trade.isPaper && <Badge variant="outline">paper</Badge>}
          </span>
          {trade.adherenceScore !== undefined && trade.adherenceScore !== null && (
            <AdherenceChip score={trade.adherenceScore} />
          )}
        </div>
        <div className="flex items-center justify-between text-sm">
          <span className="text-muted-foreground">
            {formatDate(trade.openedAt)}
            {trade.setupTag ? ` · ${trade.setupTag}` : ''}
          </span>
          <span className="flex items-center gap-3">
            {trade.rRealised !== undefined && trade.rRealised !== null ? (
              <RValue value={trade.rRealised} />
            ) : (
              <span className="text-xs text-muted-foreground">
                {trade.status === 'open' ? 'open' : '—'}
              </span>
            )}
            <span className={cn('font-mono text-sm tabular-nums', pnlTone(trade.realisedPnlMinor))}>
              {formatMinor(trade.realisedPnlMinor, trade.currency)}
            </span>
          </span>
        </div>
      </Card>
    </Link>
  )
}

export default function JournalPage() {
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()

  const [status, setStatus] = React.useState<string>(ALL)
  const [hasPlan, setHasPlan] = React.useState<string>(ALL)
  const [grade, setGrade] = React.useState<string>(ALL)
  const [emotion, setEmotion] = React.useState<string>(ALL)
  const [paperOnly, setPaperOnly] = React.useState(false)
  const [search, setSearch] = React.useState('')
  const [debouncedSearch, setDebouncedSearch] = React.useState('')
  const [page, setPage] = React.useState(1)
  const [quickLogOpen, setQuickLogOpen] = React.useState(searchParams.get('quicklog') === '1')

  React.useEffect(() => {
    const handle = setTimeout(() => {
      setDebouncedSearch(search)
      setPage(1)
    }, 300)
    return () => clearTimeout(handle)
  }, [search])

  // The command palette deep-links Quick Log via /journal?quicklog=1.
  React.useEffect(() => {
    if (searchParams.get('quicklog') === '1') {
      setQuickLogOpen(true)
      setSearchParams({}, { replace: true })
    }
  }, [searchParams, setSearchParams])

  const filters = {
    ...(status !== ALL ? { status: status as TradeStatus } : {}),
    ...(hasPlan !== ALL ? { hasPlan: hasPlan === 'yes' } : {}),
    ...(grade !== ALL ? { grade } : {}),
    ...(emotion !== ALL ? { emotion: emotion as EmotionTag } : {}),
    ...(paperOnly ? { isPaper: true } : {}),
    ...(debouncedSearch ? { search: debouncedSearch } : {}),
    page,
    pageSize: 25,
  }

  const { data, isLoading } = useGetTradesQuery(filters)
  const { data: inboxCount } = useGetInboxCountQuery()

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Journal"
        description="Plans, decisions and executed trades — your process on the record."
        actions={
          <>
            <Button variant="outline" size="sm" asChild>
              <Link to="/journal/inbox">
                <InboxIcon aria-hidden />
                Inbox
                {(inboxCount?.count ?? 0) > 0 && (
                  <Badge className="ml-1 px-1.5">{inboxCount!.count}</Badge>
                )}
              </Link>
            </Button>
            <Button variant="outline" size="sm" onClick={() => setQuickLogOpen(true)}>
              <ZapIcon aria-hidden />
              Quick Log
            </Button>
            <Button asChild size="sm">
              <Link to="/journal/plans/new">
                <PlusIcon aria-hidden />
                New Plan
              </Link>
            </Button>
          </>
        }
      />

      <Tabs defaultValue="trades">
        <TabsList>
          <TabsTrigger value="trades">Trades</TabsTrigger>
          <TabsTrigger value="analytics">Analytics</TabsTrigger>
        </TabsList>

        <TabsContent value="trades" className="mt-4 flex flex-col gap-4">
          {/* Filter bar */}
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative">
              <SearchIcon className="absolute top-2.5 left-2.5 size-4 text-muted-foreground" />
              <Input
                placeholder="Search symbol or setup…"
                className="h-9 w-48 pl-8"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>
            <Select value={status} onValueChange={(v) => { setStatus(v); setPage(1) }}>
              <SelectTrigger className="h-9">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Any status</SelectItem>
                <SelectItem value="open">Open</SelectItem>
                <SelectItem value="closed">Closed</SelectItem>
              </SelectContent>
            </Select>
            <Select value={hasPlan} onValueChange={(v) => { setHasPlan(v); setPage(1) }}>
              <SelectTrigger className="h-9">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Plan or not</SelectItem>
                <SelectItem value="yes">Planned</SelectItem>
                <SelectItem value="no">Unplanned</SelectItem>
              </SelectContent>
            </Select>
            <Select value={grade} onValueChange={(v) => { setGrade(v); setPage(1) }}>
              <SelectTrigger className="h-9">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Any grade</SelectItem>
                {['A', 'B', 'C', 'D', 'F'].map((g) => (
                  <SelectItem key={g} value={g}>
                    Grade {g}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={emotion} onValueChange={(v) => { setEmotion(v); setPage(1) }}>
              <SelectTrigger className="h-9">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Any emotion</SelectItem>
                {(['calm', 'fomo', 'tilt', 'bored', 'rushed'] as const).map((e) => (
                  <SelectItem key={e} value={e}>
                    {e}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Label className="flex items-center gap-2 text-sm text-muted-foreground">
              <Switch checked={paperOnly} onCheckedChange={(v) => { setPaperOnly(v); setPage(1) }} />
              Paper only
            </Label>
          </div>

          {/* Content */}
          {isLoading ? (
            <div className="flex flex-col gap-2">
              {[...Array(5).keys()].map((i) => (
                <Skeleton key={i} className="h-12 w-full" />
              ))}
            </div>
          ) : !data || data.items.length === 0 ? (
            <EmptyState
              icon={NotebookPenIcon}
              title="No trades match"
              hint="Trades appear here once you promote a plan, quick-log an unplanned fill, or import your broker history."
              action={
                <Button asChild variant="outline" size="sm">
                  <Link to="/journal/import">Import trade history</Link>
                </Button>
              }
            />
          ) : (
            <>
              {/* Desktop table */}
              <div className="hidden overflow-x-auto rounded-xl border md:block">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Date</TableHead>
                      <TableHead>Instrument</TableHead>
                      <TableHead>Direction</TableHead>
                      <TableHead>Setup</TableHead>
                      <TableHead className="text-right">R</TableHead>
                      <TableHead className="text-right">P&amp;L</TableHead>
                      <TableHead>Adherence</TableHead>
                      <TableHead>Tags</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.items.map((trade) => (
                      <TableRow
                        key={trade.id}
                        className="cursor-pointer"
                        onClick={() => void navigate(`/journal/trades/${trade.id}`)}
                      >
                        <TableCell className="whitespace-nowrap text-muted-foreground">
                          {formatDate(trade.openedAt)}
                        </TableCell>
                        <TableCell className="font-medium">
                          {trade.instrumentSymbol}
                          {trade.isPaper && (
                            <Badge variant="outline" className="ml-1.5">
                              paper
                            </Badge>
                          )}
                        </TableCell>
                        <TableCell>
                          <Badge variant={trade.direction === 'long' ? 'success' : 'destructive'}>
                            {trade.direction}
                          </Badge>
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground">
                          {trade.setupTag ?? (trade.planId ? 'planned' : 'unplanned')}
                        </TableCell>
                        <TableCell className="text-right">
                          {trade.rRealised !== undefined && trade.rRealised !== null ? (
                            <RValue value={trade.rRealised} />
                          ) : (
                            <span className="text-xs text-muted-foreground">
                              {trade.status === 'open' ? 'open' : '—'}
                            </span>
                          )}
                        </TableCell>
                        <TableCell
                          className={cn(
                            'text-right font-mono tabular-nums',
                            pnlTone(trade.realisedPnlMinor),
                          )}
                        >
                          {formatMinor(trade.realisedPnlMinor, trade.currency)}
                        </TableCell>
                        <TableCell>
                          {trade.adherenceScore !== undefined && trade.adherenceScore !== null ? (
                            <AdherenceChip score={trade.adherenceScore} />
                          ) : (
                            <span className="text-xs text-muted-foreground">—</span>
                          )}
                        </TableCell>
                        <TableCell>
                          <span className="flex flex-wrap gap-1">
                            {trade.tags.slice(0, 3).map((tag) => (
                              <Badge key={tag} variant="secondary">
                                {tag}
                              </Badge>
                            ))}
                          </span>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>

              {/* Mobile cards */}
              <div className="flex flex-col gap-2 md:hidden">
                {data.items.map((trade) => (
                  <TradeRowMobile key={trade.id} trade={trade} />
                ))}
              </div>

              {/* Pagination */}
              <div className="flex items-center justify-between text-sm text-muted-foreground">
                <span>
                  {data.totalCount} trade{data.totalCount === 1 ? '' : 's'}
                </span>
                {totalPages > 1 && (
                  <span className="flex items-center gap-2">
                    <Button
                      variant="outline"
                      size="icon"
                      disabled={page <= 1}
                      onClick={() => setPage((p) => p - 1)}
                      aria-label="Previous page"
                    >
                      <ChevronLeftIcon />
                    </Button>
                    Page {page} of {totalPages}
                    <Button
                      variant="outline"
                      size="icon"
                      disabled={page >= totalPages}
                      onClick={() => setPage((p) => p + 1)}
                      aria-label="Next page"
                    >
                      <ChevronRightIcon />
                    </Button>
                  </span>
                )}
              </div>
            </>
          )}
        </TabsContent>

        <TabsContent value="analytics" className="mt-4">
          <AnalyticsSection isPaper={paperOnly ? true : undefined} />
        </TabsContent>
      </Tabs>

      <QuickLogDialog open={quickLogOpen} onOpenChange={setQuickLogOpen} />
    </div>
  )
}
