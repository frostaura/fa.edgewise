/**
 * Settings section barrel — the router mounts these by name (see app/router.tsx).
 * Each section lives in its own file; this file only re-exports.
 */

export { ProfileSection } from '@/features/settings/ProfileSection'
export { RiskSection } from '@/features/settings/RiskSection'
export { BucketsSection } from '@/features/settings/BucketsSection'

// Exchange sync (Polymarket / Binance / Coinbase) lives in its own module.
export { IntegrationsSection } from '@/features/settings/IntegrationsSection'

// Settings → LLM is owned by the coach vertical; mounted here as-is.
export { LlmSettingsSection as LlmSection } from '@/features/coach'

export { SecuritySection } from '@/features/settings/SecuritySection'
export { ExportSection } from '@/features/settings/ExportSection'
