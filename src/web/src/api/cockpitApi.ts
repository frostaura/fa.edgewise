import { edgewiseApi } from '@/api/edgewiseApi'

/** Traffic-light status for a cockpit read. */
export type ReadStatus = 'green' | 'amber' | 'red'

export interface HeatRead {
  openRiskMajor: number
  heatPct?: number | null
  capPct: number
  estimated: boolean
  openTradesCount: number
  status: ReadStatus
  detail: string
}

export interface DailyPnlRead {
  pnlMinor: number
  realisedR: number
  lossCount: number
  lossCountStop: number
  stopMinor: number
  progressPct: number
  tripped: boolean
  status: ReadStatus
  detail: string
}

export interface LadderRead {
  drawdownPct: number
  rung: 'full' | 'half' | 'quarter' | 'paused'
  nextRungPct?: number | null
  locked: boolean
  status: ReadStatus
  detail: string
}

export interface CatalystItem {
  id: string
  kind: string
  severity: 'red' | 'amber'
  title: string
  at: string
  instrumentId?: string | null
  symbol?: string | null
  held: boolean
  watched: boolean
}

export interface CalendarRead {
  events: CatalystItem[]
  status: ReadStatus
  detail: string
}

export type SelfState = 'calm' | 'tired' | 'tilted' | 'rushed'

export interface SelfStateRead {
  state?: SelfState | null
  declaredAt?: string | null
  status: ReadStatus
  detail: string
}

export interface OverrideInfo {
  id: string
  kind: string
  reason: string
  at: string
}

export interface CockpitStatus {
  reads: {
    heat: HeatRead
    dailyPnl: DailyPnlRead
    ladder: LadderRead
    calendar: CalendarRead
    state: SelfStateRead
  }
  allGreen: boolean
  newPlanUnlocked: boolean
  activeOverride?: OverrideInfo | null
  today: {
    realisedR: number
    pnlMinor: number
    tradesCount: number
    dailyStopProgressPct: number
  }
}

export const cockpitApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getCockpitStatus: build.query<CockpitStatus, void>({
      query: () => '/cockpit/status',
      providesTags: ['CockpitStatus'],
    }),
    setCockpitState: build.mutation<CockpitStatus, { state: SelfState }>({
      query: (body) => ({ url: '/cockpit/state', method: 'POST', body }),
      invalidatesTags: ['CockpitStatus'],
    }),
    overrideCockpit: build.mutation<CockpitStatus, { reason: string }>({
      query: (body) => ({ url: '/cockpit/override', method: 'POST', body }),
      invalidatesTags: ['CockpitStatus'],
    }),
    getCatalysts: build.query<CatalystItem[], { days?: number } | void>({
      query: (args) => ({
        url: '/cockpit/catalysts',
        params: args && args.days !== undefined ? { days: args.days } : undefined,
      }),
      providesTags: ['CockpitStatus'],
    }),
    logDecision: build.mutation<
      unknown,
      { kind: 'enter' | 'exit' | 'skip'; instrumentId?: string; reason: string }
    >({
      // Owned by the Journal vertical — called defensively (may 404 until it lands).
      query: (body) => ({ url: '/decisions', method: 'POST', body }),
    }),
  }),
})

export const {
  useGetCockpitStatusQuery,
  useSetCockpitStateMutation,
  useOverrideCockpitMutation,
  useGetCatalystsQuery,
  useLogDecisionMutation,
} = cockpitApi
