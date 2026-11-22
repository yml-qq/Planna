import { describe, expect, it } from 'vitest'
import { densityClass, navLinks, normalizeTheme } from './prefs'

const allVisible = { habits: true, focus: true, goals: true, notes: true, calendar: true }
const noneVisible = { habits: false, focus: false, goals: false, notes: false, calendar: false }

describe('preferences', () => {
  it('normalizes theme values and falls back to auto', () => {
    expect(normalizeTheme('dark')).toBe('dark')
    expect(normalizeTheme('light')).toBe('light')
    expect(normalizeTheme('auto')).toBe('auto')
    expect(normalizeTheme(null)).toBe('auto')
    expect(normalizeTheme('sepia')).toBe('auto')
  })

  it('builds navigation links from module visibility', () => {
    expect(navLinks(allVisible).map((link) => link.id)).toEqual([
      'tasks',
      'calendar',
      'focus',
      'goals',
      'habits',
      'notes'
    ])
    expect(navLinks(noneVisible)).toEqual([{ id: 'tasks', label: '任务' }])
  })

  it('maps density to a root class', () => {
    expect(densityClass(true)).toBe('app--compact')
    expect(densityClass(false)).toBe('')
  })
})
