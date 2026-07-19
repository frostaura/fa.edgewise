import type {
  ConditionOperator,
  IndicatorKind,
  Pipeline,
  RuleCondition,
  RuleTree,
  StrategyState,
  TrailMethod,
} from '@/api/labApi'

/* ------------------------------------------------------------ metadata */

export interface IndicatorParamMeta {
  key: string
  label: string
  defaultValue: number
}

export interface IndicatorMeta {
  label: string
  hint: string
  params: IndicatorParamMeta[]
  /** Sensible starting operator/operand for a fresh row. */
  defaultOperator: ConditionOperator
  defaultOperand: number
}

export const INDICATORS: Record<IndicatorKind, IndicatorMeta> = {
  priceVsMa: {
    label: 'Price vs MA',
    hint: '(close − SMA) / SMA. > 0 means price above the MA.',
    params: [{ key: 'period', label: 'MA period', defaultValue: 20 }],
    defaultOperator: 'gt',
    defaultOperand: 0,
  },
  rsiBand: {
    label: 'RSI',
    hint: 'Classic RSI. Try < 30 (oversold) or within 40–60 (pullback).',
    params: [{ key: 'period', label: 'RSI period', defaultValue: 14 }],
    defaultOperator: 'lt',
    defaultOperand: 30,
  },
  breakoutNBarHigh: {
    label: 'Breakout of N-bar high',
    hint: '(close − max high of previous N bars) / that max. > 0 = breakout.',
    params: [{ key: 'n', label: 'Lookback bars', defaultValue: 20 }],
    defaultOperator: 'gt',
    defaultOperand: 0,
  },
  volumeVsAvg: {
    label: 'Volume vs average',
    hint: 'volume / SMA(volume). 1.5 = 150% of average volume.',
    params: [{ key: 'period', label: 'Avg period', defaultValue: 20 }],
    defaultOperator: 'gt',
    defaultOperand: 1.5,
  },
  macdCross: {
    label: 'MACD histogram',
    hint: 'macd − signal. Cross above 0 = bullish cross.',
    params: [
      { key: 'fast', label: 'Fast', defaultValue: 12 },
      { key: 'slow', label: 'Slow', defaultValue: 26 },
      { key: 'signal', label: 'Signal', defaultValue: 9 },
    ],
    defaultOperator: 'crossAbove',
    defaultOperand: 0,
  },
  priceVsBollinger: {
    label: 'Price vs Bollinger',
    hint: '+1 at the upper band, −1 at the lower band, 0 at the middle.',
    params: [
      { key: 'period', label: 'Period', defaultValue: 20 },
      { key: 'sd', label: 'Std dev', defaultValue: 2 },
    ],
    defaultOperator: 'lt',
    defaultOperand: -1,
  },
}

export const OPERATORS: Record<ConditionOperator, string> = {
  gt: 'greater than',
  lt: 'less than',
  crossAbove: 'crosses above',
  crossBelow: 'crosses below',
  within: 'within',
}

export const TRAIL_METHODS: Record<TrailMethod, string> = {
  none: 'No trail',
  atrMult: 'ATR multiple',
  pctFromPeak: '% from peak',
}

export const MAX_CONDITIONS = 6

/* ------------------------------------------------------------ form model */

/** Numeric inputs are kept as strings while editing; '' means "unset". */
export interface ConditionForm {
  indicator: IndicatorKind
  params: Record<string, string>
  operator: ConditionOperator
  operand: string
  operand2: string
  enabled: boolean
}

export interface ExitsForm {
  breakevenAtR: string
  partialTakeAtR: string
  partialPct: string
  trailMethod: TrailMethod
  trailParam: string
  timeStopBars: string
  stopMode: 'atr' | 'pct'
  stopAtrMult: string
  stopPct: string
}

export interface StrategyForm {
  direction: 'long' | 'short'
  conditions: ConditionForm[]
  exits: ExitsForm
  riskPctPerTrade: string
}

export function emptyCondition(indicator: IndicatorKind = 'priceVsMa'): ConditionForm {
  const meta = INDICATORS[indicator]
  return {
    indicator,
    params: Object.fromEntries(meta.params.map((p) => [p.key, String(p.defaultValue)])),
    operator: meta.defaultOperator,
    operand: String(meta.defaultOperand),
    operand2: '',
    enabled: true,
  }
}

export function defaultForm(): StrategyForm {
  return {
    direction: 'long',
    conditions: [emptyCondition()],
    exits: {
      breakevenAtR: '',
      partialTakeAtR: '',
      partialPct: '',
      trailMethod: 'none',
      trailParam: '',
      timeStopBars: '',
      stopMode: 'atr',
      stopAtrMult: '2',
      stopPct: '',
    },
    riskPctPerTrade: '0.5',
  }
}

/* ----------------------------------------------------------- serializer */

const num = (value: string): number | undefined => {
  if (value.trim() === '') return undefined
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : undefined
}

/** Form state -> canonical RuleTreeJson shape (drops empty optionals). */
export function formToRuleTree(form: StrategyForm, name?: string): RuleTree {
  const conditions: RuleCondition[] = form.conditions.map((c) => {
    const params: Record<string, number> = {}
    for (const [key, value] of Object.entries(c.params)) {
      const parsed = num(value)
      if (parsed !== undefined) params[key] = parsed
    }
    const condition: RuleCondition = {
      indicator: c.indicator,
      params,
      operator: c.operator,
      operand: num(c.operand) ?? 0,
      enabled: c.enabled,
    }
    if (c.operator === 'within') condition.operand2 = num(c.operand2) ?? 0
    return condition
  })

  const exits: RuleTree['exits'] = { trailMethod: form.exits.trailMethod }
  const breakeven = num(form.exits.breakevenAtR)
  if (breakeven !== undefined) exits.breakevenAtR = breakeven
  const partialAt = num(form.exits.partialTakeAtR)
  if (partialAt !== undefined) {
    exits.partialTakeAtR = partialAt
    exits.partialPct = num(form.exits.partialPct) ?? 50
  }
  if (form.exits.trailMethod !== 'none') {
    exits.trailParam = num(form.exits.trailParam) ?? 0
  }
  const timeStop = num(form.exits.timeStopBars)
  if (timeStop !== undefined) exits.timeStopBars = Math.round(timeStop)
  if (form.exits.stopMode === 'atr') {
    const stopAtr = num(form.exits.stopAtrMult)
    if (stopAtr !== undefined) exits.stopAtrMult = stopAtr
  } else {
    const stopPct = num(form.exits.stopPct)
    if (stopPct !== undefined) exits.stopPct = stopPct
  }

  const tree: RuleTree = {
    direction: form.direction,
    combinator: 'and',
    conditions,
    exits,
    risk: { riskPctPerTrade: num(form.riskPctPerTrade) ?? 0.5 },
  }
  if (name) tree.name = name
  return tree
}

/** RuleTreeJson -> form state (unknown/missing fields get safe defaults). */
export function ruleTreeToForm(tree: RuleTree): StrategyForm {
  const conditions: ConditionForm[] = (tree.conditions ?? []).slice(0, MAX_CONDITIONS).map((c) => {
    const meta = INDICATORS[c.indicator] ?? INDICATORS.priceVsMa
    const params: Record<string, string> = {}
    for (const p of meta.params) {
      const value = c.params?.[p.key]
      params[p.key] = String(value ?? p.defaultValue)
    }
    return {
      indicator: c.indicator,
      params,
      operator: c.operator,
      operand: String(c.operand ?? 0),
      operand2: c.operand2 === null || c.operand2 === undefined ? '' : String(c.operand2),
      enabled: c.enabled !== false,
    }
  })

  const exits = tree.exits ?? { trailMethod: 'none' as TrailMethod }
  const stopMode: 'atr' | 'pct' = exits.stopPct !== undefined && exits.stopPct !== null ? 'pct' : 'atr'

  return {
    direction: tree.direction === 'short' ? 'short' : 'long',
    conditions: conditions.length > 0 ? conditions : [emptyCondition()],
    exits: {
      breakevenAtR: exits.breakevenAtR == null ? '' : String(exits.breakevenAtR),
      partialTakeAtR: exits.partialTakeAtR == null ? '' : String(exits.partialTakeAtR),
      partialPct: exits.partialPct == null ? '' : String(exits.partialPct),
      trailMethod: exits.trailMethod ?? 'none',
      trailParam: exits.trailParam == null ? '' : String(exits.trailParam),
      timeStopBars: exits.timeStopBars == null ? '' : String(exits.timeStopBars),
      stopMode,
      stopAtrMult: exits.stopAtrMult == null ? (stopMode === 'atr' ? '2' : '') : String(exits.stopAtrMult),
      stopPct: exits.stopPct == null ? '' : String(exits.stopPct),
    },
    riskPctPerTrade: String(tree.risk?.riskPctPerTrade ?? 0.5),
  }
}

/* ------------------------------------------------------ pipeline mapping */

export const STATE_ORDER: StrategyState[] = ['draft', 'backtested', 'paper', 'tinyLive', 'live']

export const STATE_LABELS: Record<StrategyState, string> = {
  draft: 'Draft',
  backtested: 'Backtested',
  paper: 'Paper',
  tinyLive: 'Tiny live',
  live: 'Live',
}

/** Pipeline color ramp Draft -> Live using only design-system badge variants. */
export const STATE_BADGE_VARIANT: Record<
  StrategyState,
  'outline' | 'secondary' | 'default' | 'warning' | 'success'
> = {
  draft: 'outline',
  backtested: 'secondary',
  paper: 'default',
  tinyLive: 'warning',
  live: 'success',
}

export function nextForwardState(state: StrategyState): StrategyState | null {
  const index = STATE_ORDER.indexOf(state)
  return index >= 0 && index < STATE_ORDER.length - 1 ? STATE_ORDER[index + 1] : null
}

export interface GateItem {
  key: string
  label: string
  /** 0..1 completion for progress bars. */
  progress: number
  met: boolean
  detail: string
}

/**
 * Maps pipeline gate progress onto the checklist shown for the NEXT forward
 * transition. Empty when there is no forward state (already live).
 */
export function gateChecklist(pipeline: Pipeline): GateItem[] {
  const next = nextForwardState(pipeline.state)
  if (!next) return []
  const g = pipeline.gates

  switch (next) {
    case 'backtested':
      return [
        {
          key: 'honestBacktest',
          label: 'Honest backtest of current version',
          progress: g.honestDoneBacktests > 0 ? 1 : 0,
          met: g.honestDoneBacktests > 0,
          detail:
            g.honestDoneBacktests > 0
              ? `${g.honestDoneBacktests} completed backtest(s) with out-of-sample evidence`
              : 'Run a backtest that is not exploratory (needs OOS evidence)',
        },
      ]
    case 'paper':
      return [
        {
          key: 'ready',
          label: 'No gate — start paper trading when ready',
          progress: 1,
          met: true,
          detail: 'Backtested strategies can move to paper at any time.',
        },
      ]
    case 'tinyLive': {
      const trades = Math.min(g.paperTrades / g.paperTradesRequired, 1)
      const adherence = g.avgAdherence ?? 0
      return [
        {
          key: 'paperTrades',
          label: `Paper trades ${g.paperTrades}/${g.paperTradesRequired}`,
          progress: trades,
          met: g.paperTrades >= g.paperTradesRequired,
          detail: 'Closed paper trades tagged to this strategy',
        },
        {
          key: 'adherence',
          label: `Avg adherence ${g.avgAdherence?.toFixed(1) ?? '—'} / ${g.adherenceRequired}`,
          progress: Math.min(adherence / g.adherenceRequired, 1),
          met: adherence >= g.adherenceRequired,
          detail: 'Average plan-adherence score across those paper trades',
        },
      ]
    }
    case 'live': {
      const trades = Math.min(g.liveTrades / g.liveTradesRequired, 1)
      const expectancy = g.liveExpectancyR ?? 0
      return [
        {
          key: 'liveTrades',
          label: `Live trades ${g.liveTrades}/${g.liveTradesRequired}`,
          progress: trades,
          met: g.liveTrades >= g.liveTradesRequired,
          detail: 'Closed tiny-live trades tagged to this strategy',
        },
        {
          key: 'expectancy',
          label: `Live expectancy ${g.liveExpectancyR != null ? `${expectancy.toFixed(2)}R` : '—'}`,
          progress: expectancy > 0 ? 1 : 0,
          met: expectancy > 0,
          detail: 'Average realised R across live trades must be positive',
        },
      ]
    }
    default:
      return []
  }
}
