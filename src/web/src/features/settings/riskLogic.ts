import { z } from 'zod'

import type { LadderThresholds, RiskProfile, RiskProfileValues } from '@/api/riskProfilesApi'

// ---------------------------------------------------------------------------
// Risk profile form logic. The API speaks FRACTIONS (0.01 = 1%); the form
// displays PERCENT (1 = 1%). Conversion lives here so it is unit-testable.
// ---------------------------------------------------------------------------

/** 0.0075 → 0.75 (fraction on the wire → percent for display). */
export function fractionToPct(fraction: number): number {
  return Math.round(fraction * 100 * 1e6) / 1e6
}

/** 0.75 → 0.0075 (percent from an input → fraction for the wire). */
export function pctToFraction(pct: number): number {
  return Math.round((pct / 100) * 1e8) / 1e8
}

/** Form model — every *Pct field is in percent-display units. */
export const riskProfileFormSchema = z
  .object({
    name: z.string().trim().min(1, 'Give the profile a name.').max(60, 'Keep the name under 60 characters.'),
    riskPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(10, 'More than 10% per trade is not risk management.'),
    heatCapPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(50, 'Keep total heat at or below 50%.'),
    clusterCapPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(50, 'Keep cluster heat at or below 50%.'),
    dailyStopPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(25, 'A daily stop above 25% is not a stop.'),
    dailyLossCountStop: z.number({ error: 'Enter a number.' }).int('Whole number of losses.').min(0).max(50),
    weeklyStopPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(50, 'A weekly stop above 50% is not a stop.'),
    maxLeverage: z.number({ error: 'Enter a number.' }).positive('Must be above 0.').max(125),
    minRR: z.number({ error: 'Enter a number.' }).positive('Must be above 0.').max(50),
    maxPositions: z.number({ error: 'Enter a number.' }).int('Whole number of positions.').min(1).max(100),
    riskHalvedPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(99),
    pausedPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(99),
    paperPct: z.number({ error: 'Enter a number.' }).positive('Must be above 0%.').max(99),
  })
  .refine((v) => v.riskHalvedPct < v.pausedPct && v.pausedPct < v.paperPct, {
    message: 'Ladder rungs must increase: risk-halved < paused < paper.',
    path: ['riskHalvedPct'],
  })
  .refine((v) => v.riskPct <= v.heatCapPct, {
    message: 'Per-trade risk cannot exceed the heat cap.',
    path: ['riskPct'],
  })

export type RiskProfileForm = z.infer<typeof riskProfileFormSchema>

/** API profile (fractions) → form values (percent). */
export function profileToForm(profile: RiskProfile): RiskProfileForm {
  return {
    name: profile.name,
    riskPct: fractionToPct(profile.riskPct),
    heatCapPct: fractionToPct(profile.heatCapPct),
    clusterCapPct: fractionToPct(profile.clusterCapPct),
    dailyStopPct: fractionToPct(profile.dailyStopPct),
    dailyLossCountStop: profile.dailyLossCountStop,
    weeklyStopPct: fractionToPct(profile.weeklyStopPct),
    maxLeverage: profile.maxLeverage,
    minRR: profile.minRR,
    maxPositions: profile.maxPositions,
    riskHalvedPct: fractionToPct(profile.ladderThresholds.riskHalvedPct),
    pausedPct: fractionToPct(profile.ladderThresholds.pausedPct),
    paperPct: fractionToPct(profile.ladderThresholds.paperPct),
  }
}

/** Form values (percent) → API values (fractions). */
export function formToValues(form: RiskProfileForm): RiskProfileValues {
  const ladderThresholds: LadderThresholds = {
    riskHalvedPct: pctToFraction(form.riskHalvedPct),
    pausedPct: pctToFraction(form.pausedPct),
    paperPct: pctToFraction(form.paperPct),
  }
  return {
    riskPct: pctToFraction(form.riskPct),
    heatCapPct: pctToFraction(form.heatCapPct),
    clusterCapPct: pctToFraction(form.clusterCapPct),
    dailyStopPct: pctToFraction(form.dailyStopPct),
    dailyLossCountStop: form.dailyLossCountStop,
    weeklyStopPct: pctToFraction(form.weeklyStopPct),
    maxLeverage: form.maxLeverage,
    minRR: form.minRR,
    maxPositions: form.maxPositions,
    ladderThresholds,
  }
}

/** Preset values in form (percent) units — mirror the server's seeded defaults. */
export const riskPresets: Record<'Conservative' | 'Standard' | 'Aggressive', Omit<RiskProfileForm, 'name'>> = {
  Conservative: {
    riskPct: 0.5,
    heatCapPct: 2,
    clusterCapPct: 1.5,
    dailyStopPct: 2,
    dailyLossCountStop: 2,
    weeklyStopPct: 4,
    maxLeverage: 1,
    minRR: 2,
    maxPositions: 3,
    riskHalvedPct: 5,
    pausedPct: 10,
    paperPct: 15,
  },
  Standard: {
    riskPct: 1,
    heatCapPct: 4,
    clusterCapPct: 2,
    dailyStopPct: 3,
    dailyLossCountStop: 3,
    weeklyStopPct: 6,
    maxLeverage: 2,
    minRR: 1.5,
    maxPositions: 5,
    riskHalvedPct: 5,
    pausedPct: 10,
    paperPct: 15,
  },
  Aggressive: {
    riskPct: 2,
    heatCapPct: 6,
    clusterCapPct: 3,
    dailyStopPct: 5,
    dailyLossCountStop: 4,
    weeklyStopPct: 10,
    maxLeverage: 3,
    minRR: 1.2,
    maxPositions: 8,
    riskHalvedPct: 5,
    pausedPct: 10,
    paperPct: 15,
  },
}

/** "0.75%" — display helper that avoids float noise. */
export function formatPct(fraction: number): string {
  const pct = fractionToPct(fraction)
  return `${Number.isInteger(pct) ? pct : pct.toFixed(pct < 1 ? 2 : 1).replace(/\.?0+$/, '')}%`
}
