import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { AdherenceChip, adherenceGrade } from '@/components/domain/AdherenceChip'
import { formatR, RValue } from '@/components/domain/RValue'

describe('adherenceGrade', () => {
  it('maps scores onto the A–F ramp', () => {
    expect(adherenceGrade(100)).toBe('A')
    expect(adherenceGrade(90)).toBe('A')
    expect(adherenceGrade(89)).toBe('B')
    expect(adherenceGrade(80)).toBe('B')
    expect(adherenceGrade(75)).toBe('C')
    expect(adherenceGrade(60)).toBe('D')
    expect(adherenceGrade(59)).toBe('F')
    expect(adherenceGrade(0)).toBe('F')
  })
})

describe('AdherenceChip', () => {
  it('renders the grade letter with the success tone for high scores', () => {
    render(<AdherenceChip score={95} />)
    const chip = screen.getByText('A')
    expect(chip).toBeInTheDocument()
    expect(chip).toHaveAttribute('data-grade', 'A')
    expect(chip.className).toContain('bg-success')
    expect(chip).toHaveAccessibleName('Plan adherence 95 out of 100 (grade A)')
  })

  it('renders the destructive tone for failing scores', () => {
    render(<AdherenceChip score={12} />)
    const chip = screen.getByText('F')
    expect(chip).toHaveAttribute('data-grade', 'F')
    expect(chip.className).toContain('bg-destructive')
  })
})

describe('formatR', () => {
  it('formats signed R multiples', () => {
    expect(formatR(1.8)).toBe('+1.8R')
    expect(formatR(-0.7)).toBe('-0.7R')
    expect(formatR(0)).toBe('0.0R')
    expect(formatR(0.04)).toBe('0.0R') // rounds to zero → no misleading "+"
  })
})

describe('RValue', () => {
  it('renders wins in green, monospaced', () => {
    render(<RValue value={1.8} />)
    const el = screen.getByText('+1.8R')
    expect(el.className).toContain('text-success')
    expect(el.className).toContain('font-mono')
  })

  it('renders losses in red', () => {
    render(<RValue value={-0.7} />)
    expect(screen.getByText('-0.7R').className).toContain('text-destructive')
  })

  it('renders breakeven muted', () => {
    render(<RValue value={0} />)
    expect(screen.getByText('0.0R').className).toContain('text-muted-foreground')
  })
})
