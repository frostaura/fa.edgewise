import { authApi } from '@/api/authApi'
import { sessionCleared } from '@/features/auth/authSlice'
import { setOnline } from '@/app/uiSlice'
import type { AppStore } from '@/app/store'
import { getRefreshToken } from '@/lib/authStorage'
import { watchSystemTheme } from '@/lib/theme'

/**
 * One-time app start wiring: restore the session from the stored refresh
 * token, and keep online status + system theme in sync.
 */
export function bootstrap(store: AppStore): void {
  // --- session restore ---------------------------------------------------
  const refreshToken = getRefreshToken()
  if (refreshToken) {
    void store.dispatch(authApi.endpoints.refresh.initiate({ refreshToken }))
  } else if (store.getState().auth.status !== 'guest') {
    store.dispatch(sessionCleared())
  }

  // --- connectivity ------------------------------------------------------
  window.addEventListener('online', () => store.dispatch(setOnline(true)))
  window.addEventListener('offline', () => store.dispatch(setOnline(false)))

  // --- follow OS color-scheme changes while theme === 'system' -----------
  watchSystemTheme(() => store.getState().ui.theme)
}
