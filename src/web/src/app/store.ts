import { configureStore, createListenerMiddleware } from '@reduxjs/toolkit'
import { setupListeners } from '@reduxjs/toolkit/query'

import { edgewiseApi } from '@/api/edgewiseApi'
import authReducer, { sessionCleared, sessionEstablished } from '@/features/auth/authSlice'
import uiReducer, { setTheme } from '@/app/uiSlice'
import { clearAuthStorage, setRefreshToken, setStoredUser } from '@/lib/authStorage'
import { applyTheme, persistTheme } from '@/lib/theme'

/**
 * Side effects (localStorage, DOM class toggles) live in listener middleware
 * so reducers stay pure and the same logic runs no matter which component
 * dispatched the action.
 */
const listenerMiddleware = createListenerMiddleware()

listenerMiddleware.startListening({
  actionCreator: sessionEstablished,
  effect: (action) => {
    setRefreshToken(action.payload.refreshToken)
    if (action.payload.user) setStoredUser(action.payload.user)
  },
})

listenerMiddleware.startListening({
  actionCreator: sessionCleared,
  effect: () => {
    clearAuthStorage()
  },
})

listenerMiddleware.startListening({
  actionCreator: setTheme,
  effect: (action) => {
    persistTheme(action.payload)
    applyTheme(action.payload)
  },
})

export function makeStore() {
  const store = configureStore({
    reducer: {
      [edgewiseApi.reducerPath]: edgewiseApi.reducer,
      auth: authReducer,
      ui: uiReducer,
    },
    middleware: (getDefaultMiddleware) =>
      getDefaultMiddleware().prepend(listenerMiddleware.middleware).concat(edgewiseApi.middleware),
  })
  setupListeners(store.dispatch)
  return store
}

export const store = makeStore()

export type AppStore = ReturnType<typeof makeStore>
export type RootState = ReturnType<AppStore['getState']>
export type AppDispatch = AppStore['dispatch']
