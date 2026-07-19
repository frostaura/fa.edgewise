import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PlusIcon, Trash2Icon } from 'lucide-react'
import { toast } from 'sonner'

import type { ConditionOperator, IndicatorKind, LabInstrument, LabTimeframe } from '@/api/labApi'
import {
  useCreateBacktestMutation,
  useGetBacktestsQuery,
  useGetPipelineQuery,
  useGetStrategyQuery,
  useGetVenuePresetsQuery,
  useUpdateStrategyMutation,
} from '@/api/labApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
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
import { InstrumentPicker } from '@/features/lab/components/InstrumentPicker'
import { PipelineCard } from '@/features/lab/components/PipelineCard'
import { StateBadge } from '@/features/lab/components/StateBadge'
import {
  emptyCondition,
  formToRuleTree,
  INDICATORS,
  MAX_CONDITIONS,
  OPERATORS,
  ruleTreeToForm,
  TRAIL_METHODS,
  type ConditionForm,
  type StrategyForm,
} from '@/features/lab/ruleTree'

const numberField = 'w-24'

function ConditionRow({
  condition,
  index,
  onChange,
  onRemove,
}: {
  condition: ConditionForm
  index: number
  onChange: (next: ConditionForm) => void
  onRemove: () => void
}) {
  const meta = INDICATORS[condition.indicator]
  return (
    <div
      data-testid={`condition-row-${index}`}
      className="flex flex-col gap-2 rounded-lg border p-3"
    >
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1">
          <Label>Indicator</Label>
          <Select
            value={condition.indicator}
            onValueChange={(v) => onChange(emptyCondition(v as IndicatorKind))}
          >
            <SelectTrigger className="w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {Object.entries(INDICATORS).map(([key, m]) => (
                <SelectItem key={key} value={key}>
                  {m.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {meta.params.map((param) => (
          <div key={param.key} className="flex flex-col gap-1">
            <Label>{param.label}</Label>
            <Input
              className={numberField}
              inputMode="decimal"
              value={condition.params[param.key] ?? ''}
              onChange={(e) =>
                onChange({
                  ...condition,
                  params: { ...condition.params, [param.key]: e.target.value },
                })
              }
            />
          </div>
        ))}

        <div className="flex flex-col gap-1">
          <Label>Operator</Label>
          <Select
            value={condition.operator}
            onValueChange={(v) => onChange({ ...condition, operator: v as ConditionOperator })}
          >
            <SelectTrigger className="w-36">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {Object.entries(OPERATORS).map(([key, label]) => (
                <SelectItem key={key} value={key}>
                  {label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="flex flex-col gap-1">
          <Label>{condition.operator === 'within' ? 'From' : 'Value'}</Label>
          <Input
            className={numberField}
            inputMode="decimal"
            value={condition.operand}
            onChange={(e) => onChange({ ...condition, operand: e.target.value })}
          />
        </div>

        {condition.operator === 'within' && (
          <div className="flex flex-col gap-1">
            <Label>To</Label>
            <Input
              className={numberField}
              inputMode="decimal"
              value={condition.operand2}
              onChange={(e) => onChange({ ...condition, operand2: e.target.value })}
            />
          </div>
        )}

        <div className="ml-auto flex items-center gap-2 pb-1">
          <label className="flex items-center gap-1.5 text-sm">
            <Switch
              checked={condition.enabled}
              onCheckedChange={(v) => onChange({ ...condition, enabled: v })}
            />
            On
          </label>
          <Button size="icon" variant="ghost" aria-label="Remove condition" onClick={onRemove}>
            <Trash2Icon className="size-4" aria-hidden />
          </Button>
        </div>
      </div>
      <p className="text-xs text-muted-foreground">{meta.hint}</p>
    </div>
  )
}

export default function StrategyDetailPage() {
  const { id = '' } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { data: strategy, isLoading, error } = useGetStrategyQuery(id, { skip: !id })
  const { data: pipeline } = useGetPipelineQuery(id, { skip: !id })
  const { data: backtests = [] } = useGetBacktestsQuery({ strategyId: id }, { skip: !id })
  const { data: presets = [] } = useGetVenuePresetsQuery()
  const [updateStrategy, { isLoading: saving }] = useUpdateStrategyMutation()
  const [createBacktest, { isLoading: launching }] = useCreateBacktestMutation()

  const [form, setForm] = useState<StrategyForm | null>(null)
  const [dirty, setDirty] = useState(false)

  // Re-seed the form whenever a different strategy/version arrives
  // ("adjust state during render" pattern — avoids an effect).
  const [loadedKey, setLoadedKey] = useState('')
  const strategyKey = strategy ? `${strategy.id}:${strategy.currentVersion}` : ''
  if (strategy && strategyKey !== loadedKey) {
    setForm(ruleTreeToForm(strategy.ruleTree))
    setDirty(false)
    setLoadedKey(strategyKey)
  }

  // ---- backtest panel state
  const [btInstrument, setBtInstrument] = useState<LabInstrument | null>(null)
  const [btTimeframe, setBtTimeframe] = useState<LabTimeframe>('d1')
  const [btFrom, setBtFrom] = useState('2023-01-01')
  const [btTo, setBtTo] = useState(() => new Date().toISOString().slice(0, 10))
  const [btVenue, setBtVenue] = useState('binance')
  const [btCosts, setBtCosts] = useState({
    commissionPctPerSide: '0.1',
    spreadPct: '0.02',
    slippagePct: '0.03',
    fundingPctPer8h: '0',
  })
  const [btEquity, setBtEquity] = useState('1000000')

  const applyPreset = (venue: string) => {
    setBtVenue(venue)
    const preset = presets.find((p) => p.venue === venue)
    if (preset) {
      setBtCosts({
        commissionPctPerSide: String(preset.commissionPctPerSide),
        spreadPct: String(preset.spreadPct),
        slippagePct: String(preset.slippagePct),
        fundingPctPer8h: String(preset.fundingPctPer8h),
      })
    }
  }

  const patchForm = (patch: Partial<StrategyForm>) => {
    setForm((f) => (f ? { ...f, ...patch } : f))
    setDirty(true)
  }

  const save = async () => {
    if (!form || !strategy) return
    try {
      const updated = await updateStrategy({
        id,
        ruleTree: formToRuleTree(form, strategy.name),
      }).unwrap()
      setDirty(false)
      toast.success(
        updated.currentVersion > strategy.currentVersion
          ? `Saved as version ${updated.currentVersion}`
          : 'Saved (no rule changes — version unchanged)',
      )
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save the strategy'))
    }
  }

  const runBacktest = async () => {
    if (!btInstrument) return
    try {
      const created = await createBacktest({
        strategyId: id,
        instrumentId: btInstrument.id,
        timeframe: btTimeframe,
        from: `${btFrom}T00:00:00Z`,
        to: `${btTo}T23:59:59Z`,
        costModel: {
          venue: btVenue,
          commissionPctPerSide: Number(btCosts.commissionPctPerSide),
          spreadPct: Number(btCosts.spreadPct),
          slippagePct: Number(btCosts.slippagePct),
          fundingPctPer8h: Number(btCosts.fundingPctPer8h),
        },
        riskModel: { equityStartMinor: Number(btEquity) || 1_000_000 },
      }).unwrap()
      navigate(`/lab/backtests/${created.id}`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not start the backtest'))
    }
  }

  const conditionCount = useMemo(
    () => form?.conditions.filter((c) => c.enabled).length ?? 0,
    [form],
  )

  if (isLoading || (!strategy && !error)) {
    return <Skeleton className="h-64 w-full" />
  }

  if (error || !strategy || !form) {
    return (
      <EmptyState
        title="Strategy not found"
        hint="It may have been deleted."
        action={
          <Button asChild size="sm" variant="outline">
            <Link to="/lab/strategies">Back to strategies</Link>
          </Button>
        }
      />
    )
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={strategy.name}
        description={`Version ${strategy.currentVersion} · ${conditionCount} active condition${conditionCount === 1 ? '' : 's'}`}
        actions={
          <div className="flex items-center gap-2">
            <StateBadge state={strategy.state} />
            <Button size="sm" onClick={save} disabled={saving || !dirty}>
              {saving ? 'Saving…' : dirty ? 'Save (new version)' : 'Saved'}
            </Button>
          </div>
        }
      />

      <div className="grid gap-4 xl:grid-cols-[1fr_22rem]">
        <div className="flex flex-col gap-4">
          {/* Entry rules */}
          <Card>
            <CardHeader>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <CardTitle>Entry conditions</CardTitle>
                <div className="flex items-center gap-2">
                  <Label>Direction</Label>
                  <Select
                    value={form.direction}
                    onValueChange={(v) => patchForm({ direction: v as 'long' | 'short' })}
                  >
                    <SelectTrigger className="w-24">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="long">Long</SelectItem>
                      <SelectItem value="short">Short</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>
              <CardDescription>
                All enabled conditions must hold on the same bar (AND). Up to {MAX_CONDITIONS}{' '}
                conditions.
              </CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col gap-3">
              {form.conditions.map((condition, i) => (
                <ConditionRow
                  key={i}
                  index={i}
                  condition={condition}
                  onChange={(next) =>
                    patchForm({
                      conditions: form.conditions.map((c, j) => (j === i ? next : c)),
                    })
                  }
                  onRemove={() =>
                    patchForm({ conditions: form.conditions.filter((_, j) => j !== i) })
                  }
                />
              ))}
              <Button
                variant="outline"
                size="sm"
                className="w-fit"
                disabled={form.conditions.length >= MAX_CONDITIONS}
                onClick={() => patchForm({ conditions: [...form.conditions, emptyCondition()] })}
              >
                <PlusIcon className="size-4" aria-hidden />
                Add condition
              </Button>
            </CardContent>
          </Card>

          {/* Exit ladder */}
          <Card>
            <CardHeader>
              <CardTitle>Exit ladder</CardTitle>
              <CardDescription>
                Stop, breakeven, partials, trail and time stop — the full management plan.
              </CardDescription>
            </CardHeader>
            <CardContent className="flex flex-wrap items-end gap-3">
              <div className="flex flex-col gap-1">
                <Label>Stop mode</Label>
                <Select
                  value={form.exits.stopMode}
                  onValueChange={(v) =>
                    patchForm({ exits: { ...form.exits, stopMode: v as 'atr' | 'pct' } })
                  }
                >
                  <SelectTrigger className="w-32">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="atr">ATR multiple</SelectItem>
                    <SelectItem value="pct">Percent</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              {form.exits.stopMode === 'atr' ? (
                <div className="flex flex-col gap-1">
                  <Label>Stop (× ATR14)</Label>
                  <Input
                    className={numberField}
                    inputMode="decimal"
                    value={form.exits.stopAtrMult}
                    onChange={(e) =>
                      patchForm({ exits: { ...form.exits, stopAtrMult: e.target.value } })
                    }
                  />
                </div>
              ) : (
                <div className="flex flex-col gap-1">
                  <Label>Stop (%)</Label>
                  <Input
                    className={numberField}
                    inputMode="decimal"
                    value={form.exits.stopPct}
                    onChange={(e) =>
                      patchForm({ exits: { ...form.exits, stopPct: e.target.value } })
                    }
                  />
                </div>
              )}
              <div className="flex flex-col gap-1">
                <Label>Breakeven at R</Label>
                <Input
                  className={numberField}
                  inputMode="decimal"
                  placeholder="off"
                  value={form.exits.breakevenAtR}
                  onChange={(e) =>
                    patchForm({ exits: { ...form.exits, breakevenAtR: e.target.value } })
                  }
                />
              </div>
              <div className="flex flex-col gap-1">
                <Label>Partial at R</Label>
                <Input
                  className={numberField}
                  inputMode="decimal"
                  placeholder="off"
                  value={form.exits.partialTakeAtR}
                  onChange={(e) =>
                    patchForm({ exits: { ...form.exits, partialTakeAtR: e.target.value } })
                  }
                />
              </div>
              <div className="flex flex-col gap-1">
                <Label>Partial %</Label>
                <Input
                  className={numberField}
                  inputMode="decimal"
                  value={form.exits.partialPct}
                  onChange={(e) =>
                    patchForm({ exits: { ...form.exits, partialPct: e.target.value } })
                  }
                />
              </div>
              <div className="flex flex-col gap-1">
                <Label>Trail</Label>
                <Select
                  value={form.exits.trailMethod}
                  onValueChange={(v) =>
                    patchForm({
                      exits: { ...form.exits, trailMethod: v as StrategyForm['exits']['trailMethod'] },
                    })
                  }
                >
                  <SelectTrigger className="w-32">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {Object.entries(TRAIL_METHODS).map(([key, label]) => (
                      <SelectItem key={key} value={key}>
                        {label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              {form.exits.trailMethod !== 'none' && (
                <div className="flex flex-col gap-1">
                  <Label>{form.exits.trailMethod === 'atrMult' ? 'Trail × ATR' : 'Trail %'}</Label>
                  <Input
                    className={numberField}
                    inputMode="decimal"
                    value={form.exits.trailParam}
                    onChange={(e) =>
                      patchForm({ exits: { ...form.exits, trailParam: e.target.value } })
                    }
                  />
                </div>
              )}
              <div className="flex flex-col gap-1">
                <Label>Time stop (bars)</Label>
                <Input
                  className={numberField}
                  inputMode="numeric"
                  placeholder="off"
                  value={form.exits.timeStopBars}
                  onChange={(e) =>
                    patchForm({ exits: { ...form.exits, timeStopBars: e.target.value } })
                  }
                />
              </div>
            </CardContent>
          </Card>

          {/* Risk */}
          <Card>
            <CardHeader>
              <CardTitle>Risk</CardTitle>
              <CardDescription>Fixed-fractional sizing off the stop distance.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="flex flex-col gap-1">
                <Label>Risk % per trade</Label>
                <Input
                  className={numberField}
                  inputMode="decimal"
                  value={form.riskPctPerTrade}
                  onChange={(e) => patchForm({ riskPctPerTrade: e.target.value })}
                />
              </div>
            </CardContent>
          </Card>

          {/* Run backtest */}
          <Card>
            <CardHeader>
              <CardTitle>Run backtest</CardTitle>
              <CardDescription>
                Runs against the saved version {strategy.currentVersion}
                {dirty ? ' — save your edits first to test them.' : '.'}
              </CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col gap-3">
              <div className="flex flex-wrap items-end gap-3">
                <div className="flex flex-col gap-1">
                  <Label>Instrument</Label>
                  <InstrumentPicker value={btInstrument} onChange={setBtInstrument} />
                </div>
                <div className="flex flex-col gap-1">
                  <Label>Timeframe</Label>
                  <Select value={btTimeframe} onValueChange={(v) => setBtTimeframe(v as LabTimeframe)}>
                    <SelectTrigger className="w-24">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="h1">1H</SelectItem>
                      <SelectItem value="h4">4H</SelectItem>
                      <SelectItem value="d1">1D</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                <div className="flex flex-col gap-1">
                  <Label>From</Label>
                  <Input
                    type="date"
                    value={btFrom}
                    onChange={(e) => setBtFrom(e.target.value)}
                    className="w-36"
                  />
                </div>
                <div className="flex flex-col gap-1">
                  <Label>To</Label>
                  <Input
                    type="date"
                    value={btTo}
                    onChange={(e) => setBtTo(e.target.value)}
                    className="w-36"
                  />
                </div>
              </div>

              <div className="flex flex-wrap items-end gap-3">
                <div className="flex flex-col gap-1">
                  <Label>Cost preset</Label>
                  <Select value={btVenue} onValueChange={applyPreset}>
                    <SelectTrigger className="w-32">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {presets.map((p) => (
                        <SelectItem key={p.venue} value={p.venue}>
                          {p.venue}
                        </SelectItem>
                      ))}
                      <SelectItem value="custom">custom</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                {(
                  [
                    ['commissionPctPerSide', 'Commission %/side'],
                    ['spreadPct', 'Spread %'],
                    ['slippagePct', 'Slippage %'],
                    ['fundingPctPer8h', 'Funding %/8h'],
                  ] as const
                ).map(([key, label]) => (
                  <div key={key} className="flex flex-col gap-1">
                    <Label>{label}</Label>
                    <Input
                      className={numberField}
                      inputMode="decimal"
                      value={btCosts[key]}
                      onChange={(e) => {
                        setBtVenue('custom')
                        setBtCosts((c) => ({ ...c, [key]: e.target.value }))
                      }}
                    />
                  </div>
                ))}
                <div className="flex flex-col gap-1">
                  <Label>Equity start (minor)</Label>
                  <Input
                    className="w-32"
                    inputMode="numeric"
                    value={btEquity}
                    onChange={(e) => setBtEquity(e.target.value)}
                  />
                </div>
              </div>

              <Button className="w-fit" onClick={runBacktest} disabled={!btInstrument || launching}>
                {launching ? 'Running…' : 'Run backtest'}
              </Button>
            </CardContent>
          </Card>
        </div>

        {/* Right column: pipeline + backtest history */}
        <div className="flex flex-col gap-4">
          {pipeline && <PipelineCard pipeline={pipeline} />}

          <Card>
            <CardHeader>
              <CardTitle>Backtests</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-2">
              {backtests.length === 0 && (
                <p className="text-sm text-muted-foreground">No backtests yet.</p>
              )}
              {backtests.map((backtest) => (
                <Link
                  key={backtest.id}
                  to={`/lab/backtests/${backtest.id}`}
                  className="flex items-center justify-between gap-2 rounded-lg border p-2 text-sm hover:bg-muted"
                >
                  <span className="flex flex-col">
                    <span>
                      v{backtest.strategyVersion} · {backtest.timeframe.toUpperCase()}
                    </span>
                    <span className="text-xs text-muted-foreground">
                      {new Date(backtest.createdAt).toLocaleString()}
                    </span>
                  </span>
                  <Badge
                    variant={
                      backtest.status === 'done'
                        ? 'success'
                        : backtest.status === 'failed'
                          ? 'destructive'
                          : 'outline'
                    }
                  >
                    {backtest.status}
                  </Badge>
                </Link>
              ))}
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  )
}
