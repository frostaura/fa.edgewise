export type Theme = 'light' | 'dark' | 'system'

const THEME_KEY = 'edgewise.theme'

export function getStoredTheme(): Theme {
  try {
    const value = localStorage.getItem(THEME_KEY)
    if (value === 'light' || value === 'dark' || value === 'system') return value
  } catch {
    /* no-op */
  }
  return 'system'
}

export function persistTheme(theme: Theme): void {
  try {
    localStorage.setItem(THEME_KEY, theme)
  } catch {
    /* no-op */
  }
}

export function systemPrefersDark(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches
}

export function resolveIsDark(theme: Theme): boolean {
  return theme === 'dark' || (theme === 'system' && systemPrefersDark())
}

/** Toggle the .dark class on <html>. (Initial paint is handled in index.html.) */
export function applyTheme(theme: Theme): void {
  document.documentElement.classList.toggle('dark', resolveIsDark(theme))
}

/**
 * Re-apply when the OS scheme changes while the app follows "system".
 * Returns a cleanup function.
 */
export function watchSystemTheme(getTheme: () => Theme): () => void {
  const media = window.matchMedia('(prefers-color-scheme: dark)')
  const onChange = () => {
    if (getTheme() === 'system') applyTheme('system')
  }
  media.addEventListener('change', onChange)
  return () => media.removeEventListener('change', onChange)
}
