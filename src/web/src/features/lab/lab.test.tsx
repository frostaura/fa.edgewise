import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { BacktestResult, HonestyReport, Pipeline, RuleTree } from '@/api/labApi'
import { HonestyPanel } from '@/features/lab/components/HonestyPanel'
import {
  defaultForm,
  emptyCondition,
  formToRuleTree,
  gateChecklist,
  nextForwardState,
  ruleTreeToForm,
} from '@/features/lab/ruleTree'

/* ----------------------------------------------- rule-tree form serializer */

describe('rule-tree form serializer', () => {
  it('serializes the default form into a valid canonical shape', () => {
    const tree = formToRuleTree(defaultForm())
    expect(tree.direction).toBe('long')
    expect(tree.combinator).toBe('and')
    expect(tree.conditions).toHaveLength(1)
    expect(tree.conditions[0]).toMatchObject({
      indicator: 'priceVsMa',
      params: { period: 20 },
      operator: 'gt',
      operand: 0,
      enabled: true,
    })
    expect(tree.exits.stopAtrMult).toBe(2)
    expect(tree.exits.stopPct).toBeUndefined()
    expect(tree.risk.riskPctPerTrade).toBe(0.5)
  })

  it('roundtrips form -> tree -> form losslessly', () => {
    const form = defaultForm()
    form.direction = 'short'
    form.conditions = [
      {
        ...emptyCondition('rsiBand'),
        operator: 'within',
        operand: '40',
        operand2: '60',
      },
      { ...emptyCondition('volumeVsAvg'), operand: '1.5', enabled: false },
    ]
    form.exits = {
      breakevenAtR: '1',
      partialTakeAtR: '1.5',
      partialPct: '50',
      trailMethod: 'atrMult',
      trailParam: '2.5',
      timeStopBars: '40',
      stopMode: 'pct',
      stopAtrMult: '',
      stopPct: '3',
    }
    form.riskPctPerTrade = '0.75'

    const tree = formToRuleTree(form)
    const roundtripped = ruleTreeToForm(tree)
    expect(formToRuleTree(roundtripped)).toEqual(tree)
  })

  it('roundtrips a template-style tree -> form -> tree losslessly', () => {
    const tree: RuleTree = {
      direction: 'long',
      combinator: 'and',
      conditions: [
        {
          indicator: 'breakoutNBarHigh',
          params: { n: 20 },
          operator: 'gt',
          operand: 0,
          enabled: true,
        },
        {
          indicator: 'volumeVsAvg',
          params: { period: 20 },
          operator: 'gt',
          operand: 1.5,
          enabled: true,
        },
      ],
      exits: {
        breakevenAtR: 1,
        partialTakeAtR: 2,
        partialPct: 50,
        trailMethod: 'pctFromPeak',
        trailParam: 8,
        stopAtrMult: 1.5,
      },
      risk: { riskPctPerTrade: 0.5 },
    }
    expect(formToRuleTree(ruleTreeToForm(tree))).toEqual(tree)
  })

  it('emits operand2 only for within and drops empty optional exits', () => {
    const form = defaultForm()
    form.conditions[0].operator = 'crossAbove'
    form.conditions[0].operand2 = '99' // stale value must be dropped
    const tree = formToRuleTree(form)
    expect(tree.conditions[0].operand2).toBeUndefined()
    expect(tree.exits.breakevenAtR).toBeUndefined()
    expect(tree.exits.partialTakeAtR).toBeUndefined()
    expect(tree.exits.timeStopBars).toBeUndefined()
    expect(tree.exits.trailParam).toBeUndefined()
  })
})

/* -------------------------------------------------------- honesty panel */

const result = (n: number, expectancyR: number): BacktestResult => ({
  n,
  winRate: 0.5,
  expectancyR,
  avgR: expectancyR,
  maxDrawdownPct: 10,
  exposurePct: 40,
  trades: [],
  equityCurveMinor: [],
})

const honestyBase: HonestyReport = {
  fullSample: result(120, 0.3),
  inSample: result(80, 0.35),
  outOfSample: result(40, 0.2),
  doubledCosts: result(120, 0.15),
  wiggles: [
    {
      conditionIndex: 0,
      operandName: 'Operand',
      originalValue: 0,
      wiggledValue: 0.2,
      expectancyRDelta: -0.05,
    },
  ],
  maxWiggleSensitivityR: 0.05,
  sampleVerdict: 'thin',
  sampleSizeRedFlag: false,
  isExploratory: false,
}

describe('HonestyPanel', () => {
  it('renders IS vs OOS expectancy and the verdict badge without the red banner', () => {
    render(<HonestyPanel honesty={honestyBase} />)
    expect(screen.getByTestId('expectancy-in-sample')).toHaveTextContent('+0.35R')
    expect(screen.getByTestId('expectancy-out-of-sample')).toHaveTextContent('+0.20R')
    expect(screen.getByTestId('expectancy-2x costs')).toHaveTextContent('+0.15R')
    expect(screen.getByTestId('verdict-badge')).toHaveTextContent('thin')
    expect(screen.queryByTestId('exploratory-banner')).not.toBeInTheDocument()
  })

  it('shows the red EXPLORATORY banner and em-dashes for missing OOS', () => {
    render(
      <HonestyPanel
        honesty={{
          ...honestyBase,
          inSample: null,
          outOfSample: null,
          sampleVerdict: 'exploratory',
          sampleSizeRedFlag: true,
          isExploratory: true,
        }}
      />,
    )
    const banner = screen.getByTestId('exploratory-banner')
    expect(banner).toHaveTextContent('EXPLORATORY — no out-of-sample evidence')
    expect(screen.getByTestId('expectancy-out-of-sample')).toHaveTextContent('—')
    expect(screen.getByTestId('verdict-badge')).toHaveTextContent('exploratory')
  })
})

/* -------------------------------------------------- pipeline gate mapping */

const pipeline = (over: Partial<Pipeline['gates']>, state: Pipeline['state']): Pipeline => ({
  strategyId: 's1',
  state,
  currentVersion: 1,
  gates: {
    honestDoneBacktests: 0,
    paperTrades: 0,
    paperTradesRequired: 25,
    avgAdherence: null,
    adherenceRequired: 90,
    liveTrades: 0,
    liveTradesRequired: 30,
    liveExpectancyR: null,
    ...over,
  },
  history: [],
})

describe('pipeline gate mapping', () => {
  it('walks the forward ramp', () => {
    expect(nextForwardState('draft')).toBe('backtested')
    expect(nextForwardState('tinyLive')).toBe('live')
    expect(nextForwardState('live')).toBeNull()
  })

  it('maps the paper -> tinyLive gates with progress fractions', () => {
    const gates = gateChecklist(pipeline({ paperTrades: 10, avgAdherence: 95 }, 'paper'))
    expect(gates).toHaveLength(2)
    expect(gates[0]).toMatchObject({ key: 'paperTrades', met: false })
    expect(gates[0].progress).toBeCloseTo(10 / 25)
    expect(gates[0].label).toContain('10/25')
    expect(gates[1]).toMatchObject({ key: 'adherence', met: true, progress: 1 })
  })

  it('requires positive expectancy for the live gate', () => {
    const notYet = gateChecklist(
      pipeline({ liveTrades: 30, liveExpectancyR: -0.1 }, 'tinyLive'),
    )
    expect(notYet[0].met).toBe(true)
    expect(notYet[1].met).toBe(false)

    const ready = gateChecklist(pipeline({ liveTrades: 31, liveExpectancyR: 0.2 }, 'tinyLive'))
    expect(ready.every((g) => g.met)).toBe(true)
  })

  it('maps the draft gate off honest backtest count and is empty at live', () => {
    expect(gateChecklist(pipeline({}, 'draft'))[0].met).toBe(false)
    expect(gateChecklist(pipeline({ honestDoneBacktests: 1 }, 'draft'))[0].met).toBe(true)
    expect(gateChecklist(pipeline({}, 'live'))).toHaveLength(0)
  })
})
