/**
 * Coach feature public surface. Other verticals embed coach output via:
 *  - <InsightList tradeId={id} />            — insights on a trade detail page
 *  - <InsightCard insight={insight} />       — a single insight anywhere
 *  - <LlmSettingsSection />                  — Settings → LLM section
 *  - WeeklyReviewPage (default export file)  — /journal/review route target
 */
export { InsightList, type InsightListProps } from '@/features/coach/InsightList'
export { InsightCard, type InsightCardProps } from '@/components/domain/InsightCard'
export { LlmSettingsSection } from '@/features/coach/LlmSettingsSection'
export { default as WeeklyReviewPage } from '@/features/coach/WeeklyReviewPage'
export * from '@/features/coach/types'
