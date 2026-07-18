import type { ImportPreview } from '@/api/journalApi'

/**
 * Reducer for the 3-step CSV import wizard (upload -> map -> results).
 * Pure so it can be unit tested; the page dispatches into it.
 */

export const MAPPING_FIELDS = ['timestamp', 'symbol', 'side', 'qty', 'price', 'fee', 'feeCurrency'] as const
export type MappingField = (typeof MAPPING_FIELDS)[number]
export const REQUIRED_FIELDS: MappingField[] = ['timestamp', 'symbol', 'side', 'qty', 'price']

export type WizardStep = 'upload' | 'map' | 'done'

export interface ImportWizardState {
  step: WizardStep
  file: File | null
  preview: ImportPreview | null
  venue: string
  mapping: Record<string, string | null>
  saveMappingAs: string
}

export const initialWizardState: ImportWizardState = {
  step: 'upload',
  file: null,
  preview: null,
  venue: 'generic',
  mapping: {},
  saveMappingAs: '',
}

export type ImportWizardAction =
  | { type: 'filePicked'; file: File }
  | { type: 'previewLoaded'; preview: ImportPreview }
  | { type: 'setField'; field: MappingField; column: string | null }
  | { type: 'setVenue'; venue: string }
  | { type: 'applySavedMapping'; mappingJson: string; venue: string }
  | { type: 'setSaveMappingAs'; name: string }
  | { type: 'committed' }
  | { type: 'reset' }

export function importWizardReducer(
  state: ImportWizardState,
  action: ImportWizardAction,
): ImportWizardState {
  switch (action.type) {
    case 'filePicked':
      return { ...initialWizardState, file: action.file }
    case 'previewLoaded':
      return {
        ...state,
        step: 'map',
        preview: action.preview,
        venue: action.preview.suggestedVenue,
        mapping: { ...action.preview.suggestedMapping },
      }
    case 'setField':
      return { ...state, mapping: { ...state.mapping, [action.field]: action.column } }
    case 'setVenue':
      return { ...state, venue: action.venue }
    case 'applySavedMapping': {
      let saved: Record<string, string | null>
      try {
        saved = JSON.parse(action.mappingJson) as Record<string, string | null>
      } catch {
        return state
      }
      // Only keep saved columns that exist in the current file.
      const columns = new Set(state.preview?.columns ?? [])
      const mapping: Record<string, string | null> = { ...state.mapping }
      for (const field of MAPPING_FIELDS) {
        const column = saved[field]
        if (column && columns.has(column)) mapping[field] = column
      }
      return { ...state, venue: action.venue, mapping }
    }
    case 'setSaveMappingAs':
      return { ...state, saveMappingAs: action.name }
    case 'committed':
      return { ...state, step: 'done' }
    case 'reset':
      return initialWizardState
    default:
      return state
  }
}

/** All required fields mapped to a column? */
export function mappingComplete(mapping: Record<string, string | null>): boolean {
  return REQUIRED_FIELDS.every((field) => Boolean(mapping[field]))
}
