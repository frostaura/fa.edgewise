import { describe, expect, it } from 'vitest'

import type { ImportPreview } from '@/api/journalApi'
import {
  importWizardReducer,
  initialWizardState,
  mappingComplete,
  type ImportWizardState,
} from '@/features/journal/importMapping'

const preview: ImportPreview = {
  columns: ['Date(UTC)', 'Pair', 'Side', 'Price', 'Executed', 'Fee'],
  suggestedMapping: {
    timestamp: 'Date(UTC)',
    symbol: 'Pair',
    side: 'Side',
    qty: 'Executed',
    price: 'Price',
    fee: 'Fee',
    feeCurrency: null,
  },
  suggestedVenue: 'binance',
  sampleRows: [],
  savedMappings: [],
}

const file = new File(['a,b\n1,2'], 'fills.csv', { type: 'text/csv' })

describe('importWizardReducer', () => {
  it('moves upload -> map when a preview loads and adopts the suggestion', () => {
    let state: ImportWizardState = importWizardReducer(initialWizardState, {
      type: 'filePicked',
      file,
    })
    expect(state.step).toBe('upload')
    expect(state.file).toBe(file)

    state = importWizardReducer(state, { type: 'previewLoaded', preview })
    expect(state.step).toBe('map')
    expect(state.venue).toBe('binance')
    expect(state.mapping.qty).toBe('Executed')
  })

  it('lets a single field be remapped without touching others', () => {
    let state = importWizardReducer(initialWizardState, { type: 'previewLoaded', preview })
    state = importWizardReducer(state, { type: 'setField', field: 'qty', column: 'Fee' })
    expect(state.mapping.qty).toBe('Fee')
    expect(state.mapping.price).toBe('Price')
  })

  it('applies a saved mapping but ignores columns missing from this file', () => {
    let state = importWizardReducer(initialWizardState, { type: 'previewLoaded', preview })
    state = importWizardReducer(state, {
      type: 'applySavedMapping',
      venue: 'generic',
      mappingJson: JSON.stringify({ timestamp: 'Date(UTC)', qty: 'Amount' }), // Amount not in file
    })
    expect(state.venue).toBe('generic')
    expect(state.mapping.timestamp).toBe('Date(UTC)')
    expect(state.mapping.qty).toBe('Executed') // untouched: saved column absent
  })

  it('ignores malformed saved mapping JSON', () => {
    const state = importWizardReducer(
      importWizardReducer(initialWizardState, { type: 'previewLoaded', preview }),
      { type: 'applySavedMapping', venue: 'generic', mappingJson: 'not-json' },
    )
    expect(state.mapping.qty).toBe('Executed')
  })

  it('picking a new file resets the wizard', () => {
    let state = importWizardReducer(initialWizardState, { type: 'previewLoaded', preview })
    state = importWizardReducer(state, { type: 'committed' })
    expect(state.step).toBe('done')
    state = importWizardReducer(state, { type: 'filePicked', file })
    expect(state.step).toBe('upload')
    expect(state.preview).toBeNull()
  })
})

describe('mappingComplete', () => {
  it('requires timestamp, symbol, side, qty and price', () => {
    expect(mappingComplete(preview.suggestedMapping)).toBe(true)
    expect(mappingComplete({ ...preview.suggestedMapping, side: null })).toBe(false)
    expect(mappingComplete({})).toBe(false)
  })
})
