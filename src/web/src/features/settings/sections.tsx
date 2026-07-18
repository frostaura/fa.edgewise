import {
  DatabaseBackupIcon,
  LandmarkIcon,
  ShieldCheckIcon,
  SparklesIcon,
  UserIcon,
  WalletIcon,
  type LucideIcon,
} from 'lucide-react'

import { EmptyState } from '@/components/domain/EmptyState'

function Section({ icon, title, hint }: { icon: LucideIcon; title: string; hint: string }) {
  return <EmptyState icon={icon} title={title} hint={hint} className="min-h-48" />
}

export function ProfileSection() {
  return (
    <Section
      icon={UserIcon}
      title="Profile settings are coming"
      hint="Display name, base currency and timezone will be editable here."
    />
  )
}

export function RiskSection() {
  return (
    <Section
      icon={ShieldCheckIcon}
      title="Risk profile is coming"
      hint="Set per-trade risk, daily loss limits and max open exposure — the guardrails the coach holds you to."
    />
  )
}

export function BucketsSection() {
  return (
    <Section
      icon={WalletIcon}
      title="Buckets are coming"
      hint="Split your capital into buckets (core, swing, speculative…) with their own risk budgets."
    />
  )
}

// Exchange sync (Polymarket / Binance / Coinbase) lives in its own module.
export { IntegrationsSection } from '@/features/settings/IntegrationsSection'

export function LlmSection() {
  return (
    <Section
      icon={SparklesIcon}
      title="LLM settings are coming"
      hint="Choose the model behind the coach and control what context it may read."
    />
  )
}

export function SecuritySection() {
  return (
    <Section
      icon={LandmarkIcon}
      title="Security settings are coming"
      hint="Change your password, enroll TOTP two-factor auth (with a QR code) and manage active sessions."
    />
  )
}

export function ExportSection() {
  return (
    <Section
      icon={DatabaseBackupIcon}
      title="Export is coming"
      hint="Download your full journal, trades and settings as CSV/JSON at any time. Your data stays yours."
    />
  )
}
