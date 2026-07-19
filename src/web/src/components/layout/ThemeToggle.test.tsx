import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Provider } from 'react-redux'
import { beforeEach, describe, expect, it } from 'vitest'

import { makeStore } from '@/app/store'
import { ThemeToggle } from '@/components/layout/ThemeToggle'

describe('ThemeToggle', () => {
  beforeEach(() => {
    localStorage.clear()
    document.documentElement.classList.remove('dark')
  })

  it('toggles the .dark class on <html> and persists the choice', async () => {
    const user = userEvent.setup()
    render(
      <Provider store={makeStore()}>
        <ThemeToggle />
      </Provider>,
    )

    // System preference is light (stubbed in setup) → offers dark mode.
    await user.click(screen.getByRole('button', { name: /switch to dark theme/i }))
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(localStorage.getItem('edgewise.theme')).toBe('dark')

    // And back to light.
    await user.click(screen.getByRole('button', { name: /switch to light theme/i }))
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(localStorage.getItem('edgewise.theme')).toBe('light')
  })
})
