import { edgewiseApi } from '@/api/edgewiseApi'

// ---------------------------------------------------------------------------
// Portfolio API — money is minor units (cents) in the user's base currency,
// percentages are fractions (0.1 = 10%).
// ---------------------------------------------------------------------------

export type BucketKind = 'longTerm' | 'trading' | 'prediction' | 'cash'
export type AssetClass = 'equity' | 'etf' | 'crypto' | 'prediction' | 'cash' | 'custom'
export type CashFlowType = 'deposit' | 'withdrawal' | 'transfer' | 'ratchet' | 'dividendReceipt'
export type LadderState = 'normal' | 'riskHalved' | 'paused' | 'paperProposed'

export interface Bucket {
  id: string
  name: string
  kind: BucketKind
  targetAllocPct: number
  contributionSplitPct: number
  highWaterMarkMinor: number
  currency: string
}

export interface InstrumentRef {
  id: string
  symbol: string
  name: string
  assetClass: AssetClass
  exchange?: string | null
  currency: string
}

export interface Lot {
  id: string
  qty: number
  costMinor: number
  costCurrency: string
  acquiredAt: string
  source?: string | null
  tradeId?: string | null
}

export interface Holding {
  id: string
  bucketId: string
  instrument: InstrumentRef
  thesisNotesMd?: string | null
  manualGrowthRatePct?: number | null
  qty: number
  costBasisMinor: number
  avgCostMinor?: number | null
  valueMinor: number
  unrealisedPnlMinor: number
  unrealisedPnlPct?: number | null
  bucketWeightPct: number
  totalWeightPct: number
  stale: boolean
  price?: number | null
  priceAsOf?: string | null
  lotCount: number
  lots?: Lot[]
}

export interface CashFlow {
  id: string
  bucketId: string
  type: CashFlowType
  amountMinor: number
  currency: string
  at: string
  note?: string | null
}

export interface Dividend {
  id: string
  holdingId: string
  exDate: string
  payDate: string
  amountMinor: number
  currency: string
  reinvested: boolean
  dripLotId?: string | null
}

export interface DividendSummaryRow {
  holdingId: string
  instrumentId: string
  symbol: string
  instrumentName: string
  totalReceivedMinor: number
  trailing12MoMinor: number
  yieldOnCost?: number | null
  projected12MoMinor: number
  upcomingExDates: { at: string; title: string }[]
}

export interface DividendSummary {
  baseCurrency: string
  totalReceivedMinor: number
  projected12MoMinor: number
  holdings: DividendSummaryRow[]
}

export interface LadderThresholds {
  riskHalvedPct: number
  pausedPct: number
  paperPct: number
}

export interface LadderStateInfo {
  state: LadderState
  drawdownPct: number
  hwmMinor: number
  currentMinor: number
  thresholds: LadderThresholds
}

export interface BucketSummary {
  id: string
  name: string
  kind: BucketKind
  valueMinor: number
  targetAllocPct: number
  actualAllocPct: number
  contributionSplitPct: number
  driftFlag: boolean
  highWaterMarkMinor: number
  stale: boolean
}

export interface PortfolioSummary {
  baseCurrency: string
  totalValueMinor: number
  totalCostMinor: number
  unrealisedPnlMinor: number
  todayChangeMinor?: number | null
  perBucket: BucketSummary[]
  ladderState: LadderStateInfo
}

export interface RatchetSuggestion {
  suggestedAmountMinor: number
  profitAboveHwmMinor: number
  hwmMinor: number
  currentMinor: number
}

export interface PerformancePoint {
  date: string
  periodReturn: number
  cumulativeReturn: number
}

export interface Performance {
  basis: 'twr' | 'mwr'
  points: PerformancePoint[]
  totalReturn?: number | null
  annualizedReturn?: number | null
  xirr?: number | null
  maxDrawdown?: number | null
}

export interface AllocationSlice {
  key: string
  label: string
  valueMinor: number
  pct: number
}

export interface Allocation {
  baseCurrency: string
  totalValueMinor: number
  perBucket: AllocationSlice[]
  perAssetClass: AllocationSlice[]
}

export interface NetworthPoint {
  date: string
  equityMinor: number
  netFlowMinor: number
}

// Forecasts (fractions everywhere)
export interface ForecastAssetAssumption {
  key: string
  currentValueMinor: number
  annualGrowthPct: number
  annualVolPct?: number | null
}

export interface ForecastContribution {
  amountMinor: number
  annualIncreasePct: number
  splits: Record<string, number>
}

export interface ForecastAssumptions {
  assets: ForecastAssetAssumption[]
  contribution: ForecastContribution
  reinvest: boolean
  horizonYears: number
  haircutPct?: number | null
}

export interface DeterministicBand {
  year: number
  bearMinor: number
  baseMinor: number
  bullMinor: number
}

export interface MonteCarloBand {
  year: number
  p5Minor: number
  p25Minor: number
  p50Minor: number
  p75Minor: number
  p95Minor: number
}

export interface ForecastResult {
  ranAt: string
  monteCarlo: boolean
  paths?: number | null
  seed?: number | null
  deterministic: DeterministicBand[]
  monteCarloBands?: MonteCarloBand[] | null
}

export interface Forecast {
  id: string
  name: string
  assumptions: ForecastAssumptions
  result?: ForecastResult | null
  createdAt: string
}

export interface ForecastSeed {
  assumptions: ForecastAssumptions
  notes: { key: string; holdingId: string; symbol: string; growthCapped: boolean; cagrFromLots: boolean }[]
}

// Defensive integrations (endpoints owned by other verticals; empty when absent).
export interface AssetBar {
  ts: string
  o: number
  h: number
  l: number
  c: number
  v: number
}

export interface AssetTrade {
  id: string
  instrumentId: string
  direction: 'long' | 'short'
  status: 'open' | 'closed'
  openedAt: string
  closedAt?: string | null
  qty: number
  avgEntryPrice: number
  avgExitPrice?: number | null
}

export interface AssetZone {
  id: string
  instrumentId: string
  priceLow: number
  priceHigh: number
  strength: number
}

export interface AssetNewsItem {
  id: string
  title: string
  url: string
  source: string
  publishedAt: string
  summary?: string | null
}

export interface AssetCatalyst {
  id: string
  kind: string
  title: string
  at: string
  severity?: string
}

function asArray<T>(data: unknown): T[] {
  if (Array.isArray(data)) return data as T[]
  if (data && typeof data === 'object') {
    const items = (data as { items?: unknown }).items
    if (Array.isArray(items)) return items as T[]
  }
  return []
}

export const portfolioApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    // ---------------------------------------------------------------- buckets
    getBuckets: build.query<Bucket[], void>({
      query: () => '/buckets',
      providesTags: ['Bucket'],
    }),
    updateBucket: build.mutation<
      Bucket,
      { id: string; patch: { name?: string; targetAllocPct?: number; contributionSplitPct?: number } }
    >({
      query: ({ id, patch }) => ({ url: `/buckets/${id}`, method: 'PATCH', body: patch }),
      invalidatesTags: ['Bucket', 'Snapshot'],
    }),

    // --------------------------------------------------------------- holdings
    getHoldings: build.query<Holding[], { bucketId?: string } | void>({
      query: (params) => ({ url: '/holdings', params: params ?? undefined }),
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'Holding' as const, id })), 'Holding']
          : ['Holding'],
    }),
    getHolding: build.query<Holding, string>({
      query: (id) => `/holdings/${id}`,
      providesTags: (_r, _e, id) => [{ type: 'Holding', id }],
    }),
    createHolding: build.mutation<
      Holding,
      { bucketId: string; instrumentId: string; thesisNotesMd?: string; manualGrowthRatePct?: number }
    >({
      query: (body) => ({ url: '/holdings', method: 'POST', body }),
      invalidatesTags: ['Holding', 'Snapshot'],
    }),
    deleteHolding: build.mutation<void, string>({
      query: (id) => ({ url: `/holdings/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Holding', 'Snapshot'],
    }),
    updateThesis: build.mutation<{ id: string; thesisNotesMd?: string | null }, { id: string; thesisNotesMd: string }>({
      query: ({ id, thesisNotesMd }) => ({
        url: `/holdings/${id}/thesis`,
        method: 'PATCH',
        body: { thesisNotesMd },
      }),
      // Autosave: no invalidation churn; the detail page keeps its local copy.
    }),
    createLot: build.mutation<
      Lot,
      { holdingId: string; qty: number; costMinor: number; costCurrency?: string; acquiredAt: string; source?: string }
    >({
      query: ({ holdingId, ...body }) => ({ url: `/holdings/${holdingId}/lots`, method: 'POST', body }),
      invalidatesTags: (_r, _e, { holdingId }) => [{ type: 'Holding', id: holdingId }, 'Holding', 'Snapshot'],
    }),
    deleteLot: build.mutation<void, { holdingId: string; lotId: string }>({
      query: ({ holdingId, lotId }) => ({ url: `/holdings/${holdingId}/lots/${lotId}`, method: 'DELETE' }),
      invalidatesTags: (_r, _e, { holdingId }) => [{ type: 'Holding', id: holdingId }, 'Holding', 'Snapshot'],
    }),

    // -------------------------------------------------------------- cashflows
    getCashflows: build.query<CashFlow[], { bucketId?: string; from?: string; to?: string } | void>({
      query: (params) => ({ url: '/cashflows', params: params ?? undefined }),
      providesTags: ['Snapshot'],
    }),
    createCashflow: build.mutation<
      CashFlow | CashFlow[],
      { type: CashFlowType; bucketId: string; toBucketId?: string; amountMinor: number; at?: string; note?: string }
    >({
      query: (body) => ({ url: '/cashflows', method: 'POST', body }),
      invalidatesTags: ['Snapshot', 'Bucket'],
    }),

    // -------------------------------------------------------------- dividends
    getDividends: build.query<Dividend[], { holdingId?: string } | void>({
      query: (params) => ({ url: '/dividends', params: params ?? undefined }),
      providesTags: ['Holding'],
    }),
    getDividendSummary: build.query<DividendSummary, void>({
      query: () => '/dividends/summary',
      providesTags: ['Holding'],
    }),
    createDividend: build.mutation<
      Dividend,
      {
        holdingId: string
        exDate: string
        payDate: string
        amountMinor: number
        reinvested: boolean
        dripQty?: number
      }
    >({
      query: (body) => ({ url: '/dividends', method: 'POST', body }),
      invalidatesTags: ['Holding', 'Snapshot'],
    }),

    // -------------------------------------------------------------- portfolio
    getPortfolioSummary: build.query<PortfolioSummary, void>({
      query: () => '/portfolio/summary',
      providesTags: ['Snapshot', 'Bucket', 'Holding'],
    }),
    getLadderState: build.query<LadderStateInfo, void>({
      query: () => '/portfolio/ladder-state',
      providesTags: ['Snapshot', 'Bucket'],
    }),
    getRatchetSuggestion: build.query<RatchetSuggestion | null, void>({
      // 204 (no suggestion) arrives as an empty body: normalize to null.
      queryFn: async (_arg, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ('/portfolio/ratchet/suggestion')
        if (res.error) return { data: null }
        const data = res.data as RatchetSuggestion | undefined | null
        return { data: data && typeof data === 'object' && 'suggestedAmountMinor' in data ? data : null }
      },
      providesTags: ['Snapshot', 'Bucket'],
    }),
    acceptRatchet: build.mutation<unknown, { amountMinor: number }>({
      query: (body) => ({ url: '/portfolio/ratchet/accept', method: 'POST', body }),
      invalidatesTags: ['Snapshot', 'Bucket', 'Holding'],
    }),
    getPerformance: build.query<
      Performance,
      { bucketId?: string; basis: 'twr' | 'mwr'; from?: string; to?: string }
    >({
      query: (params) => ({ url: '/portfolio/performance', params }),
      providesTags: ['Snapshot'],
    }),
    getAllocation: build.query<Allocation, void>({
      query: () => '/portfolio/allocation',
      providesTags: ['Snapshot', 'Bucket', 'Holding'],
    }),
    getNetworth: build.query<NetworthPoint[], { from?: string; to?: string } | void>({
      query: (params) => ({ url: '/portfolio/networth', params: params ?? undefined }),
      providesTags: ['Snapshot'],
    }),
    runSnapshot: build.mutation<{ date: string; rowsWritten: number }, void>({
      query: () => ({ url: '/portfolio/snapshot/run', method: 'POST', body: {} }),
      invalidatesTags: ['Snapshot'],
    }),

    // -------------------------------------------------------------- forecasts
    getForecasts: build.query<Forecast[], void>({
      query: () => '/forecasts',
      providesTags: ['Forecast'],
    }),
    createForecast: build.mutation<Forecast, { name: string; assumptions: ForecastAssumptions }>({
      query: (body) => ({ url: '/forecasts', method: 'POST', body }),
      invalidatesTags: ['Forecast'],
    }),
    updateForecast: build.mutation<
      Forecast,
      { id: string; name: string; assumptions: ForecastAssumptions }
    >({
      query: ({ id, ...body }) => ({ url: `/forecasts/${id}`, method: 'PATCH', body }),
      invalidatesTags: ['Forecast'],
    }),
    deleteForecast: build.mutation<void, string>({
      query: (id) => ({ url: `/forecasts/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Forecast'],
    }),
    runForecast: build.mutation<
      ForecastResult,
      { id: string; monteCarlo?: boolean; paths?: number; seed?: number }
    >({
      query: ({ id, monteCarlo, paths, seed }) => ({
        url: `/forecasts/${id}/run`,
        method: 'POST',
        params: { monteCarlo, paths, seed },
        body: {},
      }),
      invalidatesTags: ['Forecast'],
    }),
    getForecastSeed: build.query<ForecastSeed, void>({
      query: () => '/forecasts/seed',
    }),

    // ----------------------------------------- defensive cross-vertical reads
    /** Instruments for the add-holding picker. Tries the market endpoints; [] when absent. */
    getPortfolioInstruments: build.query<InstrumentRef[], void>({
      queryFn: async (_arg, _api, _opts, fetchWithBQ) => {
        for (const url of ['/instruments', '/market/instruments', '/lab/instruments']) {
          const res = await fetchWithBQ({ url })
          if (!res.error) return { data: asArray<InstrumentRef>(res.data) }
        }
        return { data: [] }
      },
      providesTags: ['Instrument'],
    }),
    getAssetBars: build.query<AssetBar[], { instrumentId: string; timeframe?: string; limit?: number }>({
      queryFn: async ({ instrumentId, timeframe = 'd1', limit = 240 }, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/lab/bars', params: { instrumentId, timeframe, limit } })
        if (res.error) return { data: [] }
        return { data: asArray<AssetBar>(res.data) }
      },
    }),
    getAssetTrades: build.query<AssetTrade[], string>({
      queryFn: async (instrumentId, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/trades', params: { instrumentId } })
        if (res.error) return { data: [] }
        return { data: asArray<AssetTrade>(res.data) }
      },
    }),
    getAssetZones: build.query<AssetZone[], string>({
      queryFn: async (instrumentId, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/zones', params: { instrumentId } })
        if (res.error) return { data: [] }
        return { data: asArray<AssetZone>(res.data).filter((z) => !('archived' in z) || !(z as { archived?: boolean }).archived) }
      },
    }),
    getAssetNews: build.query<AssetNewsItem[], string>({
      queryFn: async (instrumentId, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/news', params: { instrumentId } })
        if (res.error) return { data: [] }
        return { data: asArray<AssetNewsItem>(res.data) }
      },
    }),
    getAssetCatalysts: build.query<AssetCatalyst[], string>({
      queryFn: async (instrumentId, _api, _opts, fetchWithBQ) => {
        const res = await fetchWithBQ({ url: '/calendar', params: { instrumentId } })
        if (res.error) return { data: [] }
        return { data: asArray<AssetCatalyst>(res.data) }
      },
    }),
  }),
})

export const {
  useGetBucketsQuery,
  useUpdateBucketMutation,
  useGetHoldingsQuery,
  useGetHoldingQuery,
  useCreateHoldingMutation,
  useDeleteHoldingMutation,
  useUpdateThesisMutation,
  useCreateLotMutation,
  useDeleteLotMutation,
  useGetCashflowsQuery,
  useCreateCashflowMutation,
  useGetDividendsQuery,
  useGetDividendSummaryQuery,
  useCreateDividendMutation,
  useGetPortfolioSummaryQuery,
  useGetLadderStateQuery,
  useGetRatchetSuggestionQuery,
  useAcceptRatchetMutation,
  useGetPerformanceQuery,
  useGetAllocationQuery,
  useGetNetworthQuery,
  useRunSnapshotMutation,
  useGetForecastsQuery,
  useCreateForecastMutation,
  useUpdateForecastMutation,
  useDeleteForecastMutation,
  useRunForecastMutation,
  useLazyGetForecastSeedQuery,
  useGetPortfolioInstrumentsQuery,
  useGetAssetBarsQuery,
  useGetAssetTradesQuery,
  useGetAssetZonesQuery,
  useGetAssetNewsQuery,
  useGetAssetCatalystsQuery,
} = portfolioApi
