/** Coach vertical API types (JSON camelCase, matching /api/coach and /api/brier). */

export interface InsightItem {
  text: string
  citations: string[]
}

/** Insight.ContentJson — the sections rendered by InsightCard. */
export interface InsightContent {
  observations?: InsightItem[]
  deviations?: InsightItem[]
  riskFlags?: InsightItem[]
  patternLinks?: InsightItem[]
  question?: InsightItem | null
  kudos?: InsightItem | null
  /** Extra metadata (bias code, dossier instrumentId, weekly pack weekStart/suggestedFocus). */
  code?: string
  instrumentId?: string
  weekStart?: string
  suggestedFocus?: string
}

export interface Citation {
  ref: string
  label: string
}

export type InsightType =
  | 'postMortem'
  | 'weeklyPack'
  | 'bias'
  | 'calibration'
  | 'dossier'
  | 'digest'
  | 'chatAnswer'

export type InsightFeedback = 'none' | 'up' | 'down'

export interface Insight {
  id: string
  type: InsightType
  tradeId?: string | null
  content: InsightContent | null
  citations: Citation[]
  modelTag?: string | null
  promptVersion?: string | null
  feedback: InsightFeedback
  feedbackReason?: string | null
  createdAt: string
}

export interface CoachStatus {
  enabled: boolean
  provider: string
  budgetUsedTokens: number
  budgetTokens: number
  optedOut: boolean
}

export interface WeeklyReview {
  id: string
  weekStartDate: string
  packInsightId?: string | null
  userEditsMd?: string | null
  focusCommitment?: string | null
  completedAt?: string | null
  streakCount: number
}

export interface WeeklyReviewState {
  review: WeeklyReview
  pack?: Insight | null
  pastReviews: WeeklyReview[]
}

export interface UpdateWeeklyReviewRequest {
  userEditsMd?: string
  focusCommitment?: string
  complete?: boolean
}

// -------------------------------------------------------------------- chat

export interface ChatHistoryMessage {
  role: 'user' | 'assistant'
  text: string
}

/** SSE events emitted by POST /api/coach/chat (one JSON object per `data:` line). */
export type CoachChatEvent =
  | { type: 'delta'; text: string }
  | { type: 'tool'; name: string }
  | { type: 'done'; insightId: string }
  | { type: 'offline'; reason?: string }

// ------------------------------------------------------------------- brier

export interface BrierForecast {
  id: string
  tradeId?: string | null
  question: string
  resolutionDate: string
  rulesUrl?: string | null
  pUser: number
  pMarket: number
  makerTaker?: string | null
  outcome?: boolean | null
  brierScore?: number | null
  resolvedAt?: string | null
}

export interface CreateBrierForecastRequest {
  question: string
  resolutionDate: string
  pUser: number
  pMarket: number
  rulesUrl?: string
  makerTaker?: string
  tradeId?: string
}

export interface ReliabilityBucket {
  bucketMid: number
  n: number
  avgP: number
  actualFreq: number
}

export interface CalibrationReport {
  n: number
  meanBrier: number
  meanMarketBrier: number
  reliability: ReliabilityBucket[]
  overconfidenceIndex: number
  longshotBias?: number | null
  trend: { year: number; month: number; n: number; meanBrier: number }[]
}

export interface CalibrationResponse {
  report?: CalibrationReport | null
  insight?: Insight | null
}

/** Humanises a citation ref into chip text: "trade:a1b2…" → "trade #a1b2". */
export function humanizeCitation(citation: Citation): string {
  const { ref, label } = citation
  const [kind, ...rest] = ref.split(':')
  switch (kind) {
    case 'trade':
      return `trade #${rest[0]?.slice(0, 4)}`
    case 'plan':
      return 'plan'
    case 'fill':
      return `fill #${rest[0]?.slice(0, 4)}`
    case 'adherence':
      return 'adherence'
    case 'bars':
      return `${rest[0]} bars`
    case 'stat':
      return label && label !== ref ? label : (rest[0] ?? ref).replaceAll('_', ' ')
    default:
      return label || ref
  }
}
