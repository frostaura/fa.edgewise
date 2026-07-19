import { useReducer, useState } from 'react'
import { PlayIcon, PlusIcon, SaveIcon, SparklesIcon, Trash2Icon, TrendingUpIcon } from 'lucide-react'
import { toast } from 'sonner'

import {
  useCreateForecastMutation,
  useDeleteForecastMutation,
  useGetForecastsQuery,
  useLazyGetForecastSeedQuery,
  useRunForecastMutation,
  useUpdateForecastMutation,
  type ForecastResult,
} from '@/api/portfolioApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { ForecastBandsChart } from './ForecastBandsChart'
import {
  forecastFormReducer,
  initialForecastForm,
  toAssumptions,
  validateForecastForm,
} from './lib/forecastForm'
import { formatMinor } from './lib/money'

/** Number input bound to a fraction, displayed as percent. */
function PctInput({
  value,
  onChange,
  min = -100,
  max = 200,
  className,
  'aria-label': ariaLabel,
}: {
  value: number
  onChange: (fraction: number) => void
  min?: number
  max?: number
  className?: string
  'aria-label'?: string
}) {
  return (
    <div className={`relative ${className ?? ''}`}>
      <Input
        type="number"
        step="0.5"
        min={min}
        max={max}
        aria-label={ariaLabel}
        value={Number.isFinite(value) ? Math.round(value * 1000) / 10 : 0}
        onChange={(e) => onChange(Number(e.target.value) / 100)}
        className="pr-7 text-right tabular-nums"
      />
      <span className="pointer-events-none absolute inset-y-0 right-2 flex items-center text-xs text-muted-foreground">
        %
      </span>
    </div>
  )
}

export default function ForecastPage() {
  const { data: forecasts = [] } = useGetForecastsQuery()
  const [seedTrigger, { isFetching: seeding }] = useLazyGetForecastSeedQuery()
  const [createForecast] = useCreateForecastMutation()
  const [updateForecast] = useUpdateForecastMutation()
  const [deleteForecast] = useDeleteForecastMutation()
  const [runForecast, { isLoading: running }] = useRunForecastMutation()

  const [form, dispatch] = useReducer(forecastFormReducer, initialForecastForm)
  const [selectedId, setSelectedId] = useState<string>('')
  const [name, setName] = useState('My forecast')
  const [newAssetKey, setNewAssetKey] = useState('')
  const [result, setResult] = useState<ForecastResult | null>(null)

  const selected = forecasts.find((f) => f.id === selectedId)
  const problem = validateForecastForm(form)

  const loadForecast = (id: string) => {
    const forecast = forecasts.find((f) => f.id === id)
    if (!forecast) return
    setSelectedId(id)
    setName(forecast.name)
    setResult(forecast.result ?? null)
    dispatch({ type: 'load', assumptions: forecast.assumptions })
  }

  const seedFromPortfolio = async () => {
    try {
      const seed = await seedTrigger().unwrap()
      if (seed.assumptions.assets.length === 0) {
        toast.info('No valued holdings to seed from yet.')
        return
      }
      dispatch({ type: 'seed', assumptions: seed.assumptions })
      const capped = seed.notes.filter((n) => n.growthCapped).map((n) => n.symbol)
      toast.success(
        `Seeded ${seed.assumptions.assets.length} assets from your portfolio${
          capped.length ? ` (growth capped for ${capped.join(', ')})` : ''
        }.`,
      )
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not seed from portfolio.'))
    }
  }

  const save = async (): Promise<string | null> => {
    if (problem) {
      toast.error(problem)
      return null
    }
    try {
      const body = { name: name.trim() || 'Untitled forecast', assumptions: toAssumptions(form) }
      const saved = selected
        ? await updateForecast({ id: selected.id, ...body }).unwrap()
        : await createForecast(body).unwrap()
      setSelectedId(saved.id)
      dispatch({ type: 'markSaved' })
      toast.success('Forecast saved.')
      return saved.id
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save the forecast.'))
      return null
    }
  }

  const run = async () => {
    const id = form.dirty || !selected ? await save() : selected.id
    if (!id) return
    try {
      const bands = await runForecast({
        id,
        monteCarlo: form.monteCarlo,
        paths: form.paths,
      }).unwrap()
      setResult(bands)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Forecast run failed.'))
    }
  }

  const remove = async () => {
    if (!selected) return
    try {
      await deleteForecast(selected.id).unwrap()
      setSelectedId('')
      setResult(null)
      toast.success('Forecast deleted.')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not delete the forecast.'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Forecast Studio"
        description="Where the portfolio could go under your own, fully visible assumptions."
        actions={
          <div className="flex items-center gap-2">
            {forecasts.length > 0 && (
              <Select value={selectedId} onValueChange={loadForecast}>
                <SelectTrigger className="w-44">
                  <SelectValue placeholder="Load forecast…" />
                </SelectTrigger>
                <SelectContent>
                  {forecasts.map((f) => (
                    <SelectItem key={f.id} value={f.id}>
                      {f.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            {selected && (
              <Button variant="outline" size="sm" onClick={remove} aria-label="Delete forecast">
                <Trash2Icon className="size-4" aria-hidden />
              </Button>
            )}
          </div>
        }
      />

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,26rem)_1fr]">
        {/* Assumptions panel */}
        <Card className="py-4">
          <CardContent className="flex flex-col gap-4 px-4">
            <div className="flex items-center justify-between gap-2">
              <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
                Assumptions
              </h2>
              <Button variant="outline" size="sm" onClick={seedFromPortfolio} disabled={seeding}>
                <SparklesIcon className="size-4" aria-hidden />
                Seed from portfolio
              </Button>
            </div>

            {form.assets.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Seed from your portfolio or add assets to start. Growth and volatility are annual
                percentages; splits share the monthly contribution.
              </p>
            ) : (
              <div className="flex flex-col gap-3">
                {/* Column headers only make sense when the row renders as a grid (sm+). */}
                <div className="hidden grid-cols-[1fr_5.5rem_5.5rem_5rem_auto] items-center gap-2 text-xs font-medium text-muted-foreground sm:grid">
                  <span>Asset</span>
                  <span className="text-right">Growth</span>
                  <span className="text-right">Vol</span>
                  <span className="text-right">Split</span>
                  <span />
                </div>
                {form.assets.map((asset) => (
                  <div
                    key={asset.key}
                    className="flex flex-wrap items-center gap-2 sm:grid sm:grid-cols-[1fr_5.5rem_5.5rem_5rem_auto]"
                  >
                    <div className="flex w-full min-w-0 items-baseline gap-2 sm:w-auto sm:flex-col sm:items-stretch sm:gap-0">
                      <span className="truncate text-sm font-medium">{asset.key}</span>
                      <span className="text-xs text-muted-foreground">
                        {formatMinor(asset.currentValueMinor)}
                      </span>
                    </div>
                    <PctInput
                      className="w-20 sm:w-auto"
                      aria-label={`${asset.key} annual growth`}
                      value={asset.annualGrowthPct}
                      onChange={(v) =>
                        dispatch({ type: 'setAssetField', key: asset.key, field: 'annualGrowthPct', value: v })
                      }
                    />
                    <PctInput
                      className="w-20 sm:w-auto"
                      aria-label={`${asset.key} annual volatility`}
                      value={asset.annualVolPct ?? 0}
                      min={0}
                      max={300}
                      onChange={(v) =>
                        dispatch({ type: 'setAssetField', key: asset.key, field: 'annualVolPct', value: v })
                      }
                    />
                    <PctInput
                      className="w-20 sm:w-auto"
                      aria-label={`${asset.key} contribution split`}
                      value={form.splits[asset.key] ?? 0}
                      min={0}
                      max={100}
                      onChange={(v) => dispatch({ type: 'setSplit', key: asset.key, value: v })}
                    />
                    <Button
                      variant="ghost"
                      size="sm"
                      aria-label={`Remove ${asset.key}`}
                      onClick={() => dispatch({ type: 'removeAsset', key: asset.key })}
                    >
                      <Trash2Icon className="size-4" aria-hidden />
                    </Button>
                  </div>
                ))}
              </div>
            )}

            {/* Manual assumption row — the only path for users with no valued
                holdings yet (seeding returns nothing for them). */}
            <form
              className="flex items-center gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                const key = newAssetKey.trim().toUpperCase()
                if (!key) return
                dispatch({ type: 'addAsset', key })
                setNewAssetKey('')
              }}
            >
              <Input
                value={newAssetKey}
                onChange={(e) => setNewAssetKey(e.target.value)}
                placeholder="Asset name (e.g. BTC)"
                aria-label="New asset name"
              />
              <Button
                type="submit"
                variant="outline"
                size="sm"
                disabled={newAssetKey.trim().length === 0}
              >
                <PlusIcon className="size-4" aria-hidden />
                Add asset
              </Button>
            </form>

            <div className="grid grid-cols-2 gap-3">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="fc-contribution">Contribution / year</Label>
                <Input
                  id="fc-contribution"
                  type="number"
                  min={0}
                  value={form.contributionAmountMinor / 100}
                  onChange={(e) =>
                    dispatch({ type: 'setContributionAmount', value: Math.round(Number(e.target.value) * 100) })
                  }
                  className="text-right tabular-nums"
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label>Annual increase</Label>
                <PctInput
                  value={form.contributionAnnualIncreasePct}
                  min={-100}
                  max={100}
                  onChange={(v) => dispatch({ type: 'setContributionIncrease', value: v })}
                />
              </div>
            </div>

            <div className="flex flex-col gap-1.5">
              <div className="flex items-center justify-between">
                <Label htmlFor="fc-horizon">Horizon</Label>
                <span className="text-sm font-medium tabular-nums">{form.horizonYears}y</span>
              </div>
              <input
                id="fc-horizon"
                type="range"
                min={1}
                max={20}
                step={1}
                value={form.horizonYears}
                onChange={(e) => dispatch({ type: 'setHorizon', value: Number(e.target.value) })}
                className="accent-primary"
              />
            </div>

            <div className="grid grid-cols-2 gap-3">
              <div className="flex flex-col gap-1.5">
                <Label title="Conservatism margin: positive growth is scaled down by this much before compounding">
                  Haircut
                </Label>
                <PctInput
                  value={form.haircutPct}
                  min={0}
                  max={100}
                  onChange={(v) => dispatch({ type: 'setHaircut', value: v })}
                />
              </div>
              <div className="flex flex-col justify-end gap-2 pb-1.5">
                <label className="flex items-center justify-between gap-2 text-sm">
                  Reinvest dividends
                  <Switch
                    checked={form.reinvest}
                    onCheckedChange={(v) => dispatch({ type: 'setReinvest', value: v })}
                  />
                </label>
                <label className="flex items-center justify-between gap-2 text-sm">
                  Monte Carlo
                  <Switch
                    checked={form.monteCarlo}
                    onCheckedChange={(v) => dispatch({ type: 'setMonteCarlo', value: v })}
                  />
                </label>
              </div>
            </div>

            {form.monteCarlo && (
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="fc-paths">Paths</Label>
                <Input
                  id="fc-paths"
                  type="number"
                  min={100}
                  max={20000}
                  step={100}
                  value={form.paths}
                  onChange={(e) => dispatch({ type: 'setPaths', value: Number(e.target.value) })}
                  className="text-right tabular-nums"
                />
              </div>
            )}

            <div className="flex items-center gap-2 pt-1">
              <Input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Forecast name"
                aria-label="Forecast name"
              />
              <Button variant="outline" size="sm" onClick={save}>
                <SaveIcon className="size-4" aria-hidden />
                Save
              </Button>
              <Button size="sm" onClick={run} disabled={running || !!problem}>
                <PlayIcon className="size-4" aria-hidden />
                Run
              </Button>
            </div>
            {problem && <p className="text-xs text-destructive">{problem}</p>}
          </CardContent>
        </Card>

        {/* Result bands */}
        <Card className="py-4">
          <CardContent className="flex h-full flex-col gap-3 px-4">
            <h2 className="text-sm font-semibold tracking-wide text-muted-foreground uppercase">
              Projection
            </h2>
            {result ? (
              <>
                <ForecastBandsChart result={result} currency="ZAR" />
                <p className="text-xs text-muted-foreground">
                  {result.monteCarlo
                    ? `Monte Carlo percentiles over ${result.paths} paths (seed ${result.seed ?? 0}).`
                    : 'Deterministic bear/base/bull: base growth shifted down/up by one annual standard deviation.'}{' '}
                  Haircut is applied to positive growth before compounding; contributions land
                  monthly.
                </p>
              </>
            ) : (
              <EmptyState
                icon={TrendingUpIcon}
                title="No projection yet"
                hint="Set your assumptions (or seed them from the live portfolio) and hit Run to see year-by-year bands."
                className="flex-1"
              />
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  )
}
