import { edgewiseApi } from '@/api/edgewiseApi'
import type {
  BrierForecast,
  CalibrationResponse,
  CoachStatus,
  CreateBrierForecastRequest,
  Insight,
  InsightFeedback,
  UpdateWeeklyReviewRequest,
  WeeklyReview,
  WeeklyReviewState,
} from '@/features/coach/types'

/**
 * Coach vertical endpoints (/api/coach + /api/brier). The SSE chat endpoint is NOT here —
 * it streams via fetch + ReadableStream (see features/coach/sse.ts).
 */
export const coachApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    getInsights: build.query<
      Insight[],
      { type?: string; tradeId?: string; limit?: number } | void
    >({
      query: (params) => ({ url: '/coach/insights', params: params ?? undefined }),
      providesTags: (result) =>
        result
          ? [...result.map(({ id }) => ({ type: 'Insight' as const, id })), 'Insight']
          : ['Insight'],
    }),
    submitInsightFeedback: build.mutation<
      Insight,
      { id: string; feedback: InsightFeedback; reason?: string }
    >({
      query: ({ id, feedback, reason }) => ({
        url: `/coach/insights/${id}/feedback`,
        method: 'POST',
        body: { feedback, reason },
      }),
      invalidatesTags: (_res, _err, { id }) => [{ type: 'Insight', id }],
    }),
    generatePostMortem: build.mutation<Insight, { tradeId: string }>({
      query: ({ tradeId }) => ({ url: `/coach/trades/${tradeId}/postmortem`, method: 'POST' }),
      invalidatesTags: ['Insight'],
    }),
    getWeeklyReview: build.query<WeeklyReviewState, string>({
      query: (weekStart) => `/coach/weekly-reviews/${weekStart}`,
      providesTags: (_res, _err, weekStart) => [{ type: 'WeeklyReview', id: weekStart }],
    }),
    updateWeeklyReview: build.mutation<
      WeeklyReview,
      { weekStart: string } & UpdateWeeklyReviewRequest
    >({
      query: ({ weekStart, ...body }) => ({
        url: `/coach/weekly-reviews/${weekStart}`,
        method: 'POST',
        body,
      }),
      invalidatesTags: (_res, _err, { weekStart }) => [
        { type: 'WeeklyReview', id: weekStart },
        'WeeklyReview',
      ],
    }),
    getDossier: build.query<Insight, string>({
      query: (instrumentId) => `/coach/dossier/${instrumentId}`,
      providesTags: (result) => (result ? [{ type: 'Insight', id: result.id }] : []),
    }),
    getCoachStatus: build.query<CoachStatus, void>({
      query: () => '/coach/status',
    }),

    // ------------------------------------------------------------- brier
    getBrierForecasts: build.query<BrierForecast[], { resolved?: boolean } | void>({
      query: (params) => ({ url: '/brier', params: params ?? undefined }),
      providesTags: ['Forecast'],
    }),
    createBrierForecast: build.mutation<BrierForecast, CreateBrierForecastRequest>({
      query: (body) => ({ url: '/brier', method: 'POST', body }),
      invalidatesTags: ['Forecast'],
    }),
    resolveBrierForecast: build.mutation<BrierForecast, { id: string; outcome: boolean }>({
      query: ({ id, outcome }) => ({
        url: `/brier/${id}/resolve`,
        method: 'POST',
        body: { outcome },
      }),
      invalidatesTags: ['Forecast', 'Insight'],
    }),
    deleteBrierForecast: build.mutation<void, string>({
      query: (id) => ({ url: `/brier/${id}`, method: 'DELETE' }),
      invalidatesTags: ['Forecast'],
    }),
    getCalibration: build.query<CalibrationResponse, void>({
      query: () => '/brier/calibration',
      providesTags: ['Forecast'],
    }),
  }),
})

export const {
  useGetInsightsQuery,
  useSubmitInsightFeedbackMutation,
  useGeneratePostMortemMutation,
  useGetWeeklyReviewQuery,
  useUpdateWeeklyReviewMutation,
  useGetDossierQuery,
  useGetCoachStatusQuery,
  useGetBrierForecastsQuery,
  useCreateBrierForecastMutation,
  useResolveBrierForecastMutation,
  useDeleteBrierForecastMutation,
  useGetCalibrationQuery,
} = coachApi
