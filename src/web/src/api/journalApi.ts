import { edgewiseApi } from '@/api/edgewiseApi'

/** ---- Journal contract types (JSON camelCase, enums as camelCase strings) ---- */

export type TradeDirection = 'long' | 'short'
export type TradePlanStatus = 'draft' | 'active' | 'promoted' | 'cancelled' | 'expired'
export type TradeStatus = 'open' | 'closed'
export type FillSide = 'buy' | 'sell'
export type MatchStatus = 'proposed' | 'matched' | 'confessed'
export type EmotionTag = 'none' | 'calm' | 'fomo' | 'tilt' | 'bored' | 'rushed'
export type DecisionKind = 'enter' | 'exit' | 'skip'

export interface InstrumentSummary {
  id: string
  symbol: string
  name: string
  assetClass: string
  currency: string
  exchange?: string
}

export interface Plan {
  id: string
  templateId?: string
  instrumentId: string
  bucketId: string
  riskProfileId: string
  direction: TradeDirection
  setupTag?: string
  triggerText?: string
  stopPrice: number
  targetRuleJson?: string
  sizeQty: number
  sizeOverridden: boolean
  invalidationNote?: string
  isPaper: boolean
  status: TradePlanStatus
  cockpitCheckJson?: string
  checklistConfirmedJson?: string
  createdAt: string
  instrument?: InstrumentSummary
}

export interface PlanVersion {
  version: number
  fieldsJson: string
  at: string
}

export interface CreatePlanRequest {
  templateId?: string
  instrumentId: string
  bucketId: string
  riskProfileId?: string
  direction: TradeDirection
  setupTag?: string
  triggerText?: string
  stopPrice: number
  targetRuleJson?: string
  sizeQty?: number
  sizeOverridden?: boolean
  invalidationNote?: string
  isPaper: boolean
  checklistConfirmedJson?: string
  cockpitCheckJson?: string
  entryPrice?: number
}

export interface SizePreview {
  suggestedQty: number
  rValueMinor: number
  notionalMinor: number
  bucketEquityMinor: number
  riskPct: number
}

export interface PlanTemplate {
  id: string
  userId?: string
  name: string
  description?: string
  prefillJson?: string
  checklistJson?: string
  shipped: boolean
}

export interface BucketSummary {
  id: string
  name: string
  kind: string
  currency: string
}

export interface RiskProfileSummary {
  id: string
  name: string
  riskPct: number
  heatCapPct: number
  isActive: boolean
}

export interface AccountSummary {
  id: string
  name: string
  venue: string
  bucketId: string
}

export interface PlanLookups {
  templates: PlanTemplate[]
  instruments: InstrumentSummary[]
  buckets: BucketSummary[]
  riskProfiles: RiskProfileSummary[]
  accounts: AccountSummary[]
}

export interface TradeListItem {
  id: string
  planId?: string
  instrumentId: string
  instrumentSymbol: string
  direction: TradeDirection
  status: TradeStatus
  openedAt: string
  closedAt?: string
  qty: number
  avgEntryPrice: number
  avgExitPrice?: number
  realisedPnlMinor: number
  currency: string
  rRealised?: number
  emotionTag: EmotionTag
  isPaper: boolean
  setupTag?: string
  adherenceScore?: number
  adherenceGrade?: string
  tags: string[]
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  hasMore: boolean
}

export interface Trade {
  id: string
  planId?: string
  instrumentId: string
  bucketId: string
  accountId?: string
  direction: TradeDirection
  status: TradeStatus
  openedAt: string
  closedAt?: string
  qty: number
  avgEntryPrice: number
  avgExitPrice?: number
  realisedPnlMinor: number
  feesMinor: number
  fundingMinor: number
  currency: string
  rPlanned?: number
  rRealised?: number
  maePct?: number
  mfePct?: number
  holdingSeconds?: number
  emotionTag: EmotionTag
  isPaper: boolean
  instrument?: InstrumentSummary
}

export interface Fill {
  id: string
  accountId: string
  instrumentId: string
  tradeId?: string
  side: FillSide
  qty: number
  price: number
  feeMinor: number
  feeCurrency: string
  at: string
  source: 'manual' | 'csv' | 'api'
  matchStatus: MatchStatus
}

export interface JournalEntryItem {
  id: string
  notesMd: string
  at: string
}

export interface AdherenceResult {
  id: string
  rubricVersion: number
  score: number
  grade: string
  deductionsJson?: string
  computedAt: string
}

export interface AdherenceDeduction {
  code: string
  points: number
  evidence: string
}

export interface InsightItem {
  id: string
  type: string
  contentJson: string
  createdAt: string
}

export interface TradeDetail {
  trade: Trade
  plan?: Plan
  planVersions: PlanVersion[]
  fills: Fill[]
  journalEntries: JournalEntryItem[]
  adherence?: AdherenceResult
  adherenceHistory: AdherenceResult[]
  tags: string[]
  insights?: InsightItem[]
}

export interface TradeListFilters {
  status?: TradeStatus
  instrumentId?: string
  bucketId?: string
  setupTag?: string
  emotion?: EmotionTag
  isPaper?: boolean
  hasPlan?: boolean
  grade?: string
  from?: string
  to?: string
  search?: string
  page?: number
  pageSize?: number
}

export interface InboxTradePreview {
  tradeKey: string
  direction: TradeDirection
  openedAt: string
  closedAt?: string
  qty: number
  avgEntryPrice: number
  avgExitPrice?: number
  realisedPnlMinor: number
  fillIds: string[]
}

export interface OpenTradeSummary {
  id: string
  direction: TradeDirection
  openedAt: string
  qty: number
  avgEntryPrice: number
}

export interface InboxGroup {
  instrumentId: string
  instrument?: InstrumentSummary
  accountId: string
  accountName?: string
  fills: Fill[]
  suggestedPlans: Plan[]
  tradePreview: InboxTradePreview[]
  openTrades: OpenTradeSummary[]
}

export interface ImportPreview {
  columns: string[]
  suggestedMapping: Record<string, string | null>
  suggestedVenue: string
  sampleRows: Record<string, string>[]
  savedMappings: { id: string; venue: string; name: string; mappingJson: string }[]
}

export interface ImportCommitResult {
  imported: number
  duplicates: number
  errors: string[]
}

export interface RubricRule {
  code: string
  points: number
  description: string
  isSelfReport: boolean
}

export interface Decision {
  id: string
  kind: DecisionKind
  instrumentId?: string
  planId?: string
  reason: string
  at: string
}

/** ---- Endpoints ---- */

export const journalApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    // Plans
    getPlans: build.query<Plan[], { status?: TradePlanStatus } | void>({
      query: (params) => ({ url: '/plans', params: params ?? undefined }),
      providesTags: (result) =>
        result ? [...result.map(({ id }) => ({ type: 'Plan' as const, id })), 'Plan'] : ['Plan'],
    }),
    getPlan: build.query<Plan, string>({
      query: (id) => ({ url: `/plans/${id}` }),
      providesTags: (_res, _err, id) => [{ type: 'Plan', id }],
    }),
    getPlanLookups: build.query<PlanLookups, void>({
      query: () => ({ url: '/plans/lookups' }),
      providesTags: ['Plan', 'Instrument'],
    }),
    getSizePreview: build.query<
      SizePreview,
      { bucketId: string; stopPrice: number; entryPrice: number; riskProfileId?: string }
    >({
      query: (params) => ({ url: '/plans/size-preview', params }),
    }),
    createPlan: build.mutation<Plan, CreatePlanRequest>({
      query: (body) => ({ url: '/plans', method: 'POST', body }),
      invalidatesTags: ['Plan'],
    }),
    createInstrument: build.mutation<InstrumentSummary, { symbol: string; name?: string }>({
      query: (body) => ({ url: '/plans/instruments', method: 'POST', body }),
      invalidatesTags: ['Instrument', 'Plan'],
    }),
    cancelPlan: build.mutation<Plan, string>({
      query: (id) => ({ url: `/plans/${id}/cancel`, method: 'POST' }),
      invalidatesTags: (_res, _err, id) => [{ type: 'Plan', id }, 'Plan'],
    }),
    promotePlan: build.mutation<
      Trade,
      {
        id: string
        tradeId?: string
        fills?: { accountId?: string; side: FillSide; qty: number; price: number; feeMinor: number; at: string }[]
      }
    >({
      query: ({ id, ...body }) => ({ url: `/plans/${id}/promote`, method: 'POST', body }),
      invalidatesTags: ['Plan', 'Trade'],
    }),
    getPlanTemplates: build.query<PlanTemplate[], void>({
      query: () => ({ url: '/plan-templates' }),
      providesTags: ['Plan'],
    }),

    // Trades
    getTrades: build.query<PagedResult<TradeListItem>, TradeListFilters | void>({
      query: (filters) => ({ url: '/trades', params: filters ?? undefined }),
      providesTags: (result) =>
        result
          ? [...result.items.map(({ id }) => ({ type: 'Trade' as const, id })), 'Trade']
          : ['Trade'],
    }),
    getTrade: build.query<TradeDetail, string>({
      query: (id) => ({ url: `/trades/${id}` }),
      providesTags: (_res, _err, id) => [{ type: 'Trade', id }],
    }),
    patchTrade: build.mutation<
      TradeDetail,
      { id: string; emotionTag?: EmotionTag; notes?: string; tags?: string[] }
    >({
      query: ({ id, ...body }) => ({ url: `/trades/${id}`, method: 'PATCH', body }),
      invalidatesTags: (_res, _err, { id }) => [{ type: 'Trade', id }, 'Trade'],
    }),
    closeTrade: build.mutation<
      TradeDetail,
      { id: string; closedAt?: string; avgExitPrice?: number; exitRule?: string }
    >({
      query: ({ id, ...body }) => ({ url: `/trades/${id}/close`, method: 'POST', body }),
      invalidatesTags: (_res, _err, { id }) => [{ type: 'Trade', id }, 'Trade'],
    }),
    reopenTrade: build.mutation<TradeDetail, string>({
      query: (id) => ({ url: `/trades/${id}/reopen`, method: 'POST' }),
      invalidatesTags: (_res, _err, id) => [{ type: 'Trade', id }, 'Trade'],
    }),
    recomputeAdherence: build.mutation<AdherenceResult, string>({
      query: (id) => ({ url: `/adherence/trades/${id}/recompute`, method: 'POST', body: {} }),
      invalidatesTags: (_res, _err, id) => [{ type: 'Trade', id }],
    }),
    getRubric: build.query<{ version: string; unplannedScoreCap: number; rules: RubricRule[] }, void>({
      query: () => ({ url: '/adherence/rubric' }),
    }),

    // Fills + inbox
    createFill: build.mutation<
      Fill,
      {
        accountId?: string
        instrumentId: string
        side: FillSide
        qty: number
        price: number
        feeMinor: number
        at: string
        tradeId?: string
      }
    >({
      query: (body) => ({ url: '/fills', method: 'POST', body }),
      invalidatesTags: ['Fill', 'Trade'],
    }),
    getInbox: build.query<InboxGroup[], void>({
      query: () => ({ url: '/fills/inbox' }),
      providesTags: ['Fill'],
    }),
    getInboxCount: build.query<{ count: number }, void>({
      query: () => ({ url: '/fills/inbox/count' }),
      providesTags: ['Fill'],
    }),
    matchFill: build.mutation<
      Fill,
      { id: string; planId?: string; tradeId?: string; createTrade: boolean }
    >({
      query: ({ id, ...body }) => ({ url: `/fills/${id}/match`, method: 'POST', body }),
      invalidatesTags: ['Fill', 'Trade', 'Plan'],
    }),
    confessFill: build.mutation<Fill, string>({
      query: (id) => ({ url: `/fills/${id}/confess`, method: 'POST' }),
      invalidatesTags: ['Fill', 'Trade'],
    }),
    deleteFill: build.mutation<void, string>({
      query: (id) => ({ url: `/fills/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Fill'],
    }),

    // CSV import (multipart; the file is re-sent on commit)
    importPreview: build.mutation<ImportPreview, { file: File; venue?: string }>({
      query: ({ file, venue }) => {
        const form = new FormData()
        form.append('file', file)
        if (venue) form.append('venue', venue)
        return { url: '/imports/csv/preview', method: 'POST', body: form }
      },
    }),
    importCommit: build.mutation<
      ImportCommitResult,
      {
        file: File
        mapping: Record<string, string | null>
        accountId: string
        venue: string
        saveMappingAs?: string
      }
    >({
      query: ({ file, mapping, accountId, venue, saveMappingAs }) => {
        const form = new FormData()
        form.append('file', file)
        form.append('mapping', JSON.stringify(mapping))
        form.append('accountId', accountId)
        form.append('venue', venue)
        if (saveMappingAs) form.append('saveMappingAs', saveMappingAs)
        return { url: '/imports/csv/commit', method: 'POST', body: form }
      },
      invalidatesTags: ['Fill'],
    }),

    // Decisions
    createDecision: build.mutation<
      Decision,
      { kind: DecisionKind; instrumentId?: string; planId?: string; reason: string }
    >({
      query: (body) => ({ url: '/decisions', method: 'POST', body }),
    }),
    getDecisions: build.query<Decision[], { kind?: DecisionKind } | void>({
      query: (params) => ({ url: '/decisions', params: params ?? undefined }),
    }),

    // Instrument search — the market vertical's endpoint, wired defensively:
    // callers must fall back to lookups when this errors (endpoint may not exist yet).
    searchInstruments: build.query<InstrumentSummary[], string>({
      query: (q) => ({ url: '/market/instruments/search', params: { q } }),
      transformResponse: (response: unknown): InstrumentSummary[] => {
        if (Array.isArray(response)) return response as InstrumentSummary[]
        if (response && typeof response === 'object') {
          const items = (response as { items?: unknown }).items
          if (Array.isArray(items)) return items as InstrumentSummary[]
        }
        return []
      },
    }),
  }),
})

export const {
  useGetPlansQuery,
  useGetPlanQuery,
  useGetPlanLookupsQuery,
  useGetSizePreviewQuery,
  useLazyGetSizePreviewQuery,
  useCreatePlanMutation,
  useCreateInstrumentMutation,
  useCancelPlanMutation,
  usePromotePlanMutation,
  useGetPlanTemplatesQuery,
  useGetTradesQuery,
  useGetTradeQuery,
  usePatchTradeMutation,
  useCloseTradeMutation,
  useReopenTradeMutation,
  useRecomputeAdherenceMutation,
  useGetRubricQuery,
  useCreateFillMutation,
  useGetInboxQuery,
  useGetInboxCountQuery,
  useMatchFillMutation,
  useConfessFillMutation,
  useDeleteFillMutation,
  useImportPreviewMutation,
  useImportCommitMutation,
  useCreateDecisionMutation,
  useGetDecisionsQuery,
  useSearchInstrumentsQuery,
} = journalApi
