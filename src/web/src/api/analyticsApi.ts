import { edgewiseApi } from '@/api/edgewiseApi'

/** ---- Analytics contract types (engine outputs served verbatim) ---- */

export interface AnalyticsFilters {
  from?: string
  to?: string
  isPaper?: boolean
}

export interface ExpectancyGroup {
  key: string
  n: number
  winRate: number
  avgWinR: number
  avgLossR: number
  expectancyR: number
  wilson95Lo: number
  wilson95Hi: number
  lowSample: boolean
}

export interface ExpectancyReport {
  groups: ExpectancyGroup[]
}

export interface RBin {
  from: number
  to: number
  count: number
}

export interface RDistributionReport {
  binSize: number
  bins: RBin[]
  n: number
}

export interface DrawdownPoint {
  at: string
  equityMinor: number
  peakMinor: number
  drawdownMinor: number
}

export interface DrawdownReport {
  series: DrawdownPoint[]
  maxDrawdownMinor: number
  currentDrawdownMinor: number
}

export interface WeeklyPlanRate {
  weekStart: string
  n: number
  plannedN: number
  plannedRate: number
}

export interface WeeklyAdherence {
  weekStart: string
  n: number
  avgAdherence: number
}

export interface CalendarCell {
  date: string
  sumR: number
  avgAdherence?: number
  n: number
}

export interface HourBucket {
  hourOfDay: number
  n: number
  sumR: number
}

export interface TiltSignatureReport {
  nAfterLoss: number
  expectancyAfterLoss: number
  baselineExpectancy: number
  deltaR: number
  isSignificant: boolean
}

export interface BiasCard {
  code: string
  metric: number
  threshold: number
  breached: boolean
  evidenceKeys: string[]
  details: Record<string, number>
}

export interface BiasCardsReport {
  asOf: string
  cards: BiasCard[]
}

export type ExpectancyGroupBy = 'setup' | 'instrument' | 'emotion' | 'dayOfWeek' | 'hourOfDay'

/** ---- Endpoints (all provide the 'Trade' tag so closes refresh analytics) ---- */

export const analyticsApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getExpectancy: build.query<ExpectancyReport, (AnalyticsFilters & { groupBy?: ExpectancyGroupBy }) | void>({
      query: (params) => ({ url: '/analytics/expectancy', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getRDistribution: build.query<RDistributionReport, AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/r-distribution', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getDrawdown: build.query<DrawdownReport, (AnalyticsFilters & { bucketId?: string }) | void>({
      query: (params) => ({ url: '/analytics/drawdown', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getCalendarHeatmap: build.query<CalendarCell[], (AnalyticsFilters & { year?: number }) | void>({
      query: (params) => ({ url: '/analytics/calendar-heatmap', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getSessions: build.query<HourBucket[], AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/sessions', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getTilt: build.query<TiltSignatureReport, AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/tilt', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getPtr: build.query<WeeklyPlanRate[], AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/ptr', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getAdherenceTrend: build.query<WeeklyAdherence[], AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/adherence-trend', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
    getBiasCards: build.query<BiasCardsReport, AnalyticsFilters | void>({
      query: (params) => ({ url: '/analytics/bias-cards', params: params ?? undefined }),
      providesTags: ['Trade'],
    }),
  }),
})

export const {
  useGetExpectancyQuery,
  useGetRDistributionQuery,
  useGetDrawdownQuery,
  useGetCalendarHeatmapQuery,
  useGetSessionsQuery,
  useGetTiltQuery,
  useGetPtrQuery,
  useGetAdherenceTrendQuery,
  useGetBiasCardsQuery,
} = analyticsApi
