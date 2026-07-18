import { createSlice, type PayloadAction } from '@reduxjs/toolkit'

import { getStoredTheme, type Theme } from '@/lib/theme'

export interface UiState {
  theme: Theme
  paletteOpen: boolean
  online: boolean
}

export function getInitialUiState(): UiState {
  return {
    theme: getStoredTheme(),
    paletteOpen: false,
    online: typeof navigator === 'undefined' ? true : navigator.onLine,
  }
}

const uiSlice = createSlice({
  name: 'ui',
  initialState: getInitialUiState,
  reducers: {
    setTheme(state, action: PayloadAction<Theme>) {
      state.theme = action.payload
    },
    setPaletteOpen(state, action: PayloadAction<boolean>) {
      state.paletteOpen = action.payload
    },
    togglePalette(state) {
      state.paletteOpen = !state.paletteOpen
    },
    setOnline(state, action: PayloadAction<boolean>) {
      state.online = action.payload
    },
  },
})

export const { setTheme, setPaletteOpen, togglePalette, setOnline } = uiSlice.actions
export default uiSlice.reducer

interface WithUi {
  ui: UiState
}

export const selectTheme = (state: WithUi) => state.ui.theme
export const selectPaletteOpen = (state: WithUi) => state.ui.paletteOpen
export const selectOnline = (state: WithUi) => state.ui.online
