import { edgewiseApi } from '@/api/edgewiseApi'

/* ------------------------------------------------------------------ types */

export type LabTimeframe = 'h1' | 'h4' | 'd1' | 'w1'

export interface ZoneDto {
  id: string
  instrumentId: string
  priceLow: number
  priceHigh: number
  strength: number
  sourceTimeframe: LabTimeframe
  createdAt: string
  archived: boolean
}

export interface CreateZoneBody {
  instrumentId: string
  priceLow: number
  priceHigh: number
  strength: number
  sourceTimeframe: LabTimeframe
}

export interface LabInstrument {
  id: string
  symbol: string
  name: string
  assetClass?: string
  currency?: string
  exchange?: string | null
}

export interface LabBar {
  ts: string
  o: number
  h: number
  l: number
  c: number
  v: number
}

export type IndicatorKind =
  | 'priceVsMa'
  | 'rsiBand'
  | 'breakoutNBarHigh'
  | 'volumeVsAvg'
  | 'macdCross'
  | 'priceVsBollinger'

export type ConditionOperator = 'gt' | 'lt' | 'crossAbove' | 'crossBelow' | 'within'

export type TrailMethod = 'none' | 'atrMult' | 'pctFromPeak'

export interface RuleCondition {
  indicator: IndicatorKind
  params: Record<string, number>
  operator: ConditionOperator
  operand: number
  operand2?: number | null
  enabled: boolean
}

export interface RuleExits {
  breakevenAtR?: number | null
  partialTakeAtR?: number | null
  partialPct?: number | null
  trailMethod: TrailMethod
  trailParam?: number | null
  timeStopBars?: number | null
  stopAtrMult?: number | null
  stopPct?: number | null
}

export interface RuleTree {
  name?: string
  direction: 'long' | 'short'
  combinator: 'and'
  conditions: RuleCondition[]
  exits: RuleExits
  risk: { riskPctPerTrade: number }
}

export type StrategyState = 'draft' | 'backtested' | 'paper' | 'tinyLive' | 'live'

export type BacktestStatus = 'pending' | 'running' | 'done' | 'failed'

export interface LatestBacktestSummary {
  id: string
  status: BacktestStatus
  createdAt: string
  expectancyR?: number
  trades?: number
  isExploratory?: boolean
  sampleVerdict?: string
}

export interface StrategyListItem {
  id: string
  name: string
  state: StrategyState
  currentVersion: number
  latestBacktest?: LatestBacktestSummary | null
}

export interface StrategyVersionInfo {
  version: number
  ruleTree: RuleTree
  at: string
}

export interface StrategyDetail extends StrategyListItem {
  ruleTree: RuleTree
  versions: StrategyVersionInfo[]
}

export interface StrategyTemplate {
  id: string
  name: string
  description: string
  ruleTree: RuleTree
}

export interface PipelineGates {
  honestDoneBacktests: number
  paperTrades: number
  paperTradesRequired: number
  avgAdherence?: number | null
  adherenceRequired: number
  liveTrades: number
  liveTradesRequired: number
  liveExpectancyR?: number | null
}

export interface PipelineHistoryEntry {
  fromState: StrategyState
  toState: StrategyState
  at: string
  evidence?: unknown
}

export interface Pipeline {
  strategyId: string
  state: StrategyState
  currentVersion: number
  gates: PipelineGates
  history: PipelineHistoryEntry[]
}

export interface BacktestConfig {
  venue: string
  commissionPctPerSide: number
  spreadPct: number
  slippagePct: number
  fundingPctPer8h: number
  riskPctPerTrade: number
  equityStartMinor: number
}

export interface BacktestTrade {
  entryTs: string
  exitTs: string
  direction: 'long' | 'short'
  entryPx: number
  exitPx: number
  rMultiple: number
  pnlMinor: number
}

export interface BacktestResult {
  n: number
  winRate: number
  expectancyR: number
  avgR: number
  maxDrawdownPct: number
  exposurePct: number
  trades: BacktestTrade[]
  equityCurveMinor: number[]
}

export interface WiggleEntry {
  conditionIndex: number
  operandName: string
  originalValue: number
  wiggledValue: number
  expectancyRDelta: number
}

export interface HonestyReport {
  fullSample: BacktestResult
  inSample?: BacktestResult | null
  outOfSample?: BacktestResult | null
  doubledCosts: BacktestResult
  wiggles: WiggleEntry[]
  maxWiggleSensitivityR: number
  sampleVerdict: 'adequate' | 'thin' | 'exploratory'
  sampleSizeRedFlag: boolean
  isExploratory: boolean
}

export interface Backtest {
  id: string
  strategyId: string
  strategyVersionId: string
  strategyVersion: number
  instrumentId: string
  timeframe: LabTimeframe
  rangeStart: string
  rangeEnd: string
  status: BacktestStatus
  createdAt: string
  config?: BacktestConfig
  result?: BacktestResult
  honesty?: HonestyReport
  error?: string
}

export interface VenuePreset {
  venue: string
  commissionPctPerSide: number
  spreadPct: number
  slippagePct: number
  fundingPctPer8h: number
}

export interface CreateBacktestBody {
  strategyId: string
  instrumentId: string
  timeframe: LabTimeframe
  from: string
  to: string
  costModel: {
    venue?: string
    commissionPctPerSide?: number
    spreadPct?: number
    slippagePct?: number
    fundingPctPer8h?: number
  }
  riskModel?: { riskPctPerTrade?: number; equityStartMinor?: number }
}

export interface TradeMarkerTrade {
  id: string
  instrumentId?: string
  direction?: 'long' | 'short'
  openedAt?: string
  closedAt?: string | null
  avgEntryPrice?: number
  avgExitPrice?: number | null
  isPaper?: boolean
}

export interface TradeReplay {
  tradeId: string
  instrumentId: string
  timeframe: LabTimeframe
  direction: 'long' | 'short'
  bars: LabBar[]
  entry: { ts: string; price: number }
  exit?: { ts: string; price: number } | null
  stopPrice?: number | null
  maeMfeBand?: {
    maePrice?: number | null
    mfePrice?: number | null
    maePct?: number | null
    mfePct?: number | null
  } | null
}

/* --------------------------------------------------------------- helpers */

function asArray<T>(response: unknown): T[] {
  if (Array.isArray(response)) return response as T[]
  if (response && typeof response === 'object') {
    const items = (response as { items?: unknown }).items
    if (Array.isArray(items)) return items as T[]
  }
  return []
}

/* ------------------------------------------------------------- endpoints */

export const labApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    /* ------ zones ------ */
    getZones: build.query<ZoneDto[], { instrumentId?: string; includeArchived?: boolean } | void>({
      query: (args) => ({ url: '/zones', params: args ?? undefined }),
      providesTags: (result) =>
        result ? [...result.map(({ id }) => ({ type: 'Zone' as const, id })), 'Zone'] : ['Zone'],
    }),
    createZone: build.mutation<ZoneDto, CreateZoneBody>({
      query: (body) => ({ url: '/zones', method: 'POST', body }),
      invalidatesTags: ['Zone'],
    }),
    updateZone: build.mutation<ZoneDto, { id: string; patch: Partial<CreateZoneBody> }>({
      query: ({ id, patch }) => ({ url: `/zones/${id}`, method: 'PUT', body: patch }),
      invalidatesTags: (_r, _e, { id }) => [{ type: 'Zone', id }, 'Zone'],
    }),
    toggleZoneArchive: build.mutation<ZoneDto, string>({
      query: (id) => ({ url: `/zones/${id}/archive`, method: 'POST' }),
      invalidatesTags: (_r, _e, id) => [{ type: 'Zone', id }, 'Zone'],
    }),
    deleteZone: build.mutation<void, string>({
      query: (id) => ({ url: `/zones/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Zone'],
    }),

    /* ------ chart workspace (defensive integrations) ------ */

    /**
     * Instrument search. Prefers the market vertical's endpoint when present,
     * falls back to the lab-owned search over the Instruments table.
     */
    searchLabInstruments: build.query<LabInstrument[], string>({
      queryFn: async (q, _api, _opts, fetchWithBQ) => {
        const market = await fetchWithBQ({
          url: '/market/instruments/search',
          params: q ? { q } : undefined,
        })
        if (!market.error) return { data: asArray<LabInstrument>(market.data) }
        const lab = await fetchWithBQ({ url: '/lab/instruments', params: q ? { q } : undefined })
        if (lab.error) return { error: lab.error }
        return { data: asArray<LabInstrument>(lab.data) }
      },
      providesTags: ['Instrument'],
    }),
    getLabBars: build.query<
      LabBar[],
      { instrumentId: string; timeframe: LabTimeframe; limit?: number }
    >({
      query: (params) => ({ url: '/lab/bars', params }),
    }),
    /** Trades on an instrument, for chart trade markers. Defensive: empty on any error. */
    getInstrumentTrades: build.query<TradeMarkerTrade[], string>({
      queryFn: async (instrumentId, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/trades', params: { instrumentId } })
        if (res.error) return { data: [] }
        return { data: asArray<TradeMarkerTrade>(res.data) }
      },
      providesTags: ['Trade'],
    }),

    /* ------ strategies ------ */
    getStrategies: build.query<StrategyListItem[], void>({
      query: () => '/strategies',
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'Strategy' as const, id })), 'Strategy']
          : ['Strategy'],
    }),
    getStrategy: build.query<StrategyDetail, string>({
      query: (id) => `/strategies/${id}`,
      providesTags: (_r, _e, id) => [{ type: 'Strategy', id }],
    }),
    getStrategyTemplates: build.query<StrategyTemplate[], void>({
      query: () => '/strategies/templates',
    }),
    createStrategy: build.mutation<StrategyListItem, { name: string; ruleTree: RuleTree }>({
      query: (body) => ({ url: '/strategies', method: 'POST', body }),
      invalidatesTags: ['Strategy'],
    }),
    updateStrategy: build.mutation<StrategyDetail, { id: string; name?: string; ruleTree?: RuleTree }>({
      query: ({ id, ...body }) => ({ url: `/strategies/${id}`, method: 'PUT', body }),
      invalidatesTags: (_r, _e, { id }) => [{ type: 'Strategy', id }, 'Strategy'],
    }),
    deleteStrategy: build.mutation<void, string>({
      query: (id) => ({ url: `/strategies/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Strategy'],
    }),
    getPipeline: build.query<Pipeline, string>({
      query: (id) => `/strategies/${id}/pipeline`,
      providesTags: (_r, _e, id) => [{ type: 'Strategy', id: `pipeline-${id}` }],
    }),
    transitionStrategy: build.mutation<
      Pipeline,
      { id: string; toState: StrategyState; force?: boolean; reason?: string }
    >({
      query: ({ id, ...body }) => ({ url: `/strategies/${id}/transition`, method: 'POST', body }),
      invalidatesTags: (_r, _e, { id }) => [
        { type: 'Strategy', id },
        { type: 'Strategy', id: `pipeline-${id}` },
        'Strategy',
      ],
    }),

    /* ------ backtests ------ */
    getBacktests: build.query<Backtest[], { strategyId?: string } | void>({
      query: (args) => ({ url: '/backtests', params: args ?? undefined }),
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'Backtest' as const, id })), 'Backtest']
          : ['Backtest'],
    }),
    getBacktest: build.query<Backtest, string>({
      query: (id) => `/backtests/${id}`,
      providesTags: (_r, _e, id) => [{ type: 'Backtest', id }],
    }),
    getVenuePresets: build.query<VenuePreset[], void>({
      query: () => '/backtests/presets',
    }),
    createBacktest: build.mutation<Backtest, CreateBacktestBody>({
      query: (body) => ({ url: '/backtests', method: 'POST', body }),
      invalidatesTags: ['Backtest', 'Strategy'],
    }),

    /* ------ trade replay ------ */
    getTradeReplay: build.query<TradeReplay, { tradeId: string; timeframe?: LabTimeframe }>({
      query: ({ tradeId, timeframe }) => ({
        url: `/trades/${tradeId}/replay`,
        params: timeframe ? { timeframe } : undefined,
      }),
    }),
  }),
})

export const {
  useGetZonesQuery,
  useCreateZoneMutation,
  useUpdateZoneMutation,
  useToggleZoneArchiveMutation,
  useDeleteZoneMutation,
  useSearchLabInstrumentsQuery,
  useGetLabBarsQuery,
  useGetInstrumentTradesQuery,
  useGetStrategiesQuery,
  useGetStrategyQuery,
  useGetStrategyTemplatesQuery,
  useCreateStrategyMutation,
  useUpdateStrategyMutation,
  useDeleteStrategyMutation,
  useGetPipelineQuery,
  useTransitionStrategyMutation,
  useGetBacktestsQuery,
  useGetBacktestQuery,
  useGetVenuePresetsQuery,
  useCreateBacktestMutation,
  useGetTradeReplayQuery,
} = labApi
