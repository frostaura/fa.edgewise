import { edgewiseApi } from '@/api/edgewiseApi'

// ---------------------------------------------------------------------------
// Risk profiles API — percentages are FRACTIONS on the wire (0.01 = 1%),
// matching the portfolio endpoints. Updates are versioned server-side: PUT
// inserts a new row (version + 1, same name) and returns it.
// ---------------------------------------------------------------------------

export interface LadderThresholds {
  riskHalvedPct: number
  pausedPct: number
  paperPct: number
}

export interface RiskProfile {
  id: string
  name: string
  version: number
  isActive: boolean
  riskPct: number
  heatCapPct: number
  clusterCapPct: number
  dailyStopPct: number
  dailyLossCountStop: number
  weeklyStopPct: number
  maxLeverage: number
  minRR: number
  maxPositions: number
  ladderThresholds: LadderThresholds
}

export interface RiskProfileValues {
  riskPct: number
  heatCapPct: number
  clusterCapPct: number
  dailyStopPct: number
  dailyLossCountStop: number
  weeklyStopPct: number
  maxLeverage: number
  minRR: number
  maxPositions: number
  ladderThresholds: LadderThresholds
}

export type CreateRiskProfileRequest = RiskProfileValues & { name: string }

export const riskProfilesApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getRiskProfiles: build.query<RiskProfile[], void>({
      query: () => '/risk-profiles',
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'RiskProfile' as const, id })), 'RiskProfile']
          : ['RiskProfile'],
    }),
    createRiskProfile: build.mutation<RiskProfile, CreateRiskProfileRequest>({
      query: (body) => ({ url: '/risk-profiles', method: 'POST', body }),
      invalidatesTags: ['RiskProfile', 'Plan'],
    }),
    updateRiskProfile: build.mutation<RiskProfile, { id: string; values: RiskProfileValues }>({
      query: ({ id, values }) => ({ url: `/risk-profiles/${id}`, method: 'PUT', body: values }),
      invalidatesTags: ['RiskProfile', 'Plan'],
    }),
    activateRiskProfile: build.mutation<RiskProfile, string>({
      query: (id) => ({ url: `/risk-profiles/${id}/activate`, method: 'POST', body: {} }),
      // Activation affects sizing previews and the portfolio ladder too.
      invalidatesTags: ['RiskProfile', 'Plan', 'Snapshot'],
    }),
    deleteRiskProfile: build.mutation<void, string>({
      query: (id) => ({ url: `/risk-profiles/${id}`, method: 'DELETE' }),
      invalidatesTags: ['RiskProfile', 'Plan'],
    }),
  }),
})

export const {
  useGetRiskProfilesQuery,
  useCreateRiskProfileMutation,
  useUpdateRiskProfileMutation,
  useActivateRiskProfileMutation,
  useDeleteRiskProfileMutation,
} = riskProfilesApi
