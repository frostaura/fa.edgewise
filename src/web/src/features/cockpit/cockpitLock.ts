import type { CockpitStatus, ReadStatus } from '@/api/cockpitApi'

/**
 * Pure lock/override-flow logic for the cockpit "New Plan" gate, kept out of
 * the component so it can be unit-tested.
 */

export interface LockInfo {
  /** True when planning is locked (no red-free pass and no override). */
  locked: boolean
  /** Human-readable reasons — the red reads' details. */
  reasons: string[]
  /** True when an override is active today. */
  overridden: boolean
}

const READ_LABELS: Record<string, string> = {
  heat: 'Heat',
  dailyPnl: 'Daily P&L',
  ladder: 'Ladder',
  calendar: 'Calendar',
  state: 'State',
}

/** Derives lock state + explain list from the cockpit status payload. */
export function planLock(status: CockpitStatus | undefined): LockInfo {
  if (!status) return { locked: false, reasons: [], overridden: false }
  const reasons = Object.entries(status.reads)
    .filter(([, read]) => read.status === 'red')
    .map(([key, read]) => `${READ_LABELS[key] ?? key}: ${read.detail}`)
  return {
    locked: !status.newPlanUnlocked,
    reasons,
    overridden: Boolean(status.activeOverride),
  }
}

// ------------------------------------------------------- override dialog

export interface OverrideFlowState {
  dialogOpen: boolean
  reason: string
  submitting: boolean
  error: string | null
}

export const initialOverrideFlow: OverrideFlowState = {
  dialogOpen: false,
  reason: '',
  submitting: false,
  error: null,
}

export type OverrideFlowAction =
  | { type: 'open' }
  | { type: 'cancel' }
  | { type: 'setReason'; reason: string }
  | { type: 'submit' }
  | { type: 'succeeded' }
  | { type: 'failed'; error: string }

export function overrideFlowReducer(
  state: OverrideFlowState,
  action: OverrideFlowAction,
): OverrideFlowState {
  switch (action.type) {
    case 'open':
      return { ...initialOverrideFlow, dialogOpen: true }
    case 'cancel':
      // Cannot cancel mid-flight; the request is already logged server-side.
      return state.submitting ? state : initialOverrideFlow
    case 'setReason':
      return { ...state, reason: action.reason, error: null }
    case 'submit':
      return canSubmitOverride(state) ? { ...state, submitting: true, error: null } : state
    case 'succeeded':
      return initialOverrideFlow
    case 'failed':
      return { ...state, submitting: false, error: action.error }
    default:
      return state
  }
}

/** An override needs a meaningful reason (server enforces >= 3 chars too). */
export function canSubmitOverride(state: OverrideFlowState): boolean {
  return !state.submitting && state.reason.trim().length >= 3
}

// ------------------------------------------------------------ status tones

export interface StatusTone {
  border: string
  dot: string
  text: string
  label: string
}

/** Maps a read status to border/dot/text classes (theme tokens). */
export function statusTone(status: ReadStatus): StatusTone {
  switch (status) {
    case 'red':
      return {
        border: 'border-destructive/60',
        dot: 'bg-destructive',
        text: 'text-destructive',
        label: 'Red',
      }
    case 'amber':
      return {
        border: 'border-warning/60',
        dot: 'bg-warning',
        text: 'text-warning',
        label: 'Amber',
      }
    default:
      return {
        border: 'border-success/40',
        dot: 'bg-success',
        text: 'text-success',
        label: 'Green',
      }
  }
}
