import type { ForecastAssumptions, ForecastAssetAssumption } from '@/api/portfolioApi'

/**
 * Forecast Studio assumptions form state — a pure reducer so every assumption
 * stays visible and testable. All rates are fractions (0.08 = 8%).
 */

export interface ForecastFormState {
  assets: ForecastAssetAssumption[]
  splits: Record<string, number>
  contributionAmountMinor: number
  contributionAnnualIncreasePct: number
  reinvest: boolean
  horizonYears: number
  haircutPct: number
  monteCarlo: boolean
  paths: number
  /** True once the user changed anything since the last save/run. */
  dirty: boolean
}

export const initialForecastForm: ForecastFormState = {
  assets: [],
  splits: {},
  contributionAmountMinor: 0,
  contributionAnnualIncreasePct: 0,
  reinvest: true,
  horizonYears: 10,
  haircutPct: 0.2,
  monteCarlo: false,
  paths: 1000,
  dirty: false,
}

export type ForecastFormAction =
  | { type: 'load'; assumptions: ForecastAssumptions }
  | { type: 'seed'; assumptions: ForecastAssumptions }
  | { type: 'addAsset'; key: string; currentValueMinor?: number }
  | { type: 'removeAsset'; key: string }
  | { type: 'setAssetField'; key: string; field: 'currentValueMinor' | 'annualGrowthPct' | 'annualVolPct'; value: number }
  | { type: 'setSplit'; key: string; value: number }
  | { type: 'setContributionAmount'; value: number }
  | { type: 'setContributionIncrease'; value: number }
  | { type: 'setHorizon'; value: number }
  | { type: 'setHaircut'; value: number }
  | { type: 'setReinvest'; value: boolean }
  | { type: 'setMonteCarlo'; value: boolean }
  | { type: 'setPaths'; value: number }
  | { type: 'markSaved' }

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

function fromAssumptions(assumptions: ForecastAssumptions, base: ForecastFormState): ForecastFormState {
  return {
    ...base,
    assets: assumptions.assets.map((a) => ({ ...a })),
    splits: { ...assumptions.contribution.splits },
    contributionAmountMinor: assumptions.contribution.amountMinor,
    contributionAnnualIncreasePct: assumptions.contribution.annualIncreasePct,
    reinvest: assumptions.reinvest,
    horizonYears: assumptions.horizonYears,
    haircutPct: assumptions.haircutPct ?? 0.2,
    dirty: false,
  }
}

export function forecastFormReducer(
  state: ForecastFormState,
  action: ForecastFormAction,
): ForecastFormState {
  switch (action.type) {
    case 'load':
      return fromAssumptions(action.assumptions, state)
    case 'seed':
      return { ...fromAssumptions(action.assumptions, state), dirty: true }
    case 'addAsset': {
      const key = action.key.trim()
      if (!key || state.assets.some((a) => a.key === key)) return state
      return {
        ...state,
        assets: [
          ...state.assets,
          { key, currentValueMinor: action.currentValueMinor ?? 0, annualGrowthPct: 0.08, annualVolPct: 0.15 },
        ],
        splits: { ...state.splits, [key]: 0 },
        dirty: true,
      }
    }
    case 'removeAsset': {
      const splits = { ...state.splits }
      delete splits[action.key]
      return {
        ...state,
        assets: state.assets.filter((a) => a.key !== action.key),
        splits,
        dirty: true,
      }
    }
    case 'setAssetField':
      return {
        ...state,
        assets: state.assets.map((a) =>
          a.key === action.key ? { ...a, [action.field]: action.value } : a,
        ),
        dirty: true,
      }
    case 'setSplit':
      return {
        ...state,
        splits: { ...state.splits, [action.key]: clamp(action.value, 0, 1) },
        dirty: true,
      }
    case 'setContributionAmount':
      return { ...state, contributionAmountMinor: Math.max(0, action.value), dirty: true }
    case 'setContributionIncrease':
      return { ...state, contributionAnnualIncreasePct: clamp(action.value, -1, 1), dirty: true }
    case 'setHorizon':
      return { ...state, horizonYears: clamp(Math.round(action.value), 1, 20), dirty: true }
    case 'setHaircut':
      return { ...state, haircutPct: clamp(action.value, 0, 1), dirty: true }
    case 'setReinvest':
      return { ...state, reinvest: action.value, dirty: true }
    case 'setMonteCarlo':
      return { ...state, monteCarlo: action.value }
    case 'setPaths':
      return { ...state, paths: clamp(Math.round(action.value), 100, 20000) }
    case 'markSaved':
      return { ...state, dirty: false }
    default:
      return state
  }
}

/** Assembles API assumptions from the form. */
export function toAssumptions(state: ForecastFormState): ForecastAssumptions {
  const splits: Record<string, number> = {}
  for (const asset of state.assets) splits[asset.key] = state.splits[asset.key] ?? 0
  return {
    assets: state.assets,
    contribution: {
      amountMinor: state.contributionAmountMinor,
      annualIncreasePct: state.contributionAnnualIncreasePct,
      splits,
    },
    reinvest: state.reinvest,
    horizonYears: state.horizonYears,
    haircutPct: state.haircutPct,
  }
}

/** First blocking problem with the form, or null when runnable. */
export function validateForecastForm(state: ForecastFormState): string | null {
  if (state.assets.length === 0) return 'Add at least one asset assumption.'
  for (const asset of state.assets) {
    if (asset.currentValueMinor < 0) return `${asset.key}: current value cannot be negative.`
    if (asset.annualGrowthPct < -1 || asset.annualGrowthPct > 2)
      return `${asset.key}: growth must be between -100% and +200%.`
    if (asset.annualVolPct != null && (asset.annualVolPct < 0 || asset.annualVolPct > 3))
      return `${asset.key}: volatility must be between 0% and 300%.`
  }
  return null
}
