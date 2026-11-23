import { describe, expect, it } from 'vitest'
import { MemoryBridge } from './api'
import {
  createCalendarStore,
  monthLeadingBlanks,
  monthRange,
  moveFocusIndex,
  parseOverrideLines,
  tabbableDate
} from './calendar'

function createStore(overrides: Record<string, unknown> = {}) {
  const bridge = new MemoryBridge()
  const store = createCalendarStore(bridge, {
    baseUrl: () => 'http://127.0.0.1:5080',
    token: () => 'token',
    now: () => new Date(Date.UTC(2026, 9, 15)),
    ...overrides
  })
  return { store, bridge }
}

describe('month range', () => {
  it('computes inclusive month boundaries including leap years', () => {
    expect(monthRange(2026, 10)).toEqual({ from: '2026-10-01', to: '2026-10-31' })
    expect(monthRange(2028, 2)).toEqual({ from: '2028-02-01', to: '2028-02-29' })
    expect(monthRange(2026, 12)).toEqual({ from: '2026-12-01', to: '2026-12-31' })
  })
})

describe('calendar store', () => {
  it('loads a month with weekday/weekend defaults and toggles overrides', async () => {
    const { store } = createStore()
    await store.load()
    expect(store.days.value).toHaveLength(31)
    expect(store.days.value[0]).toEqual({ date: '2026-10-01', kind: 'workday' })
    expect(store.days.value[2]).toEqual({ date: '2026-10-03', kind: 'holiday' })

    await store.toggle('2026-10-01', 'workday')
    expect(store.days.value[0]?.kind).toBe('holiday')
    await store.toggle('2026-10-01', 'holiday')
    expect(store.days.value[0]?.kind).toBe('workday')
    await store.clear('2026-10-01')
    expect(store.days.value[0]?.kind).toBe('workday')
  })

  it('keeps exactly one tabbable day per month', () => {
    const days = [
      { date: '2026-10-01', kind: 'workday' as const },
      { date: '2026-10-02', kind: 'workday' as const }
    ]
    expect(tabbableDate(days, null)).toBe('2026-10-01')
    expect(tabbableDate(days, '2026-10-02')).toBe('2026-10-02')
    expect(tabbableDate(days, '2026-11-01')).toBe('2026-10-01')
    expect(tabbableDate([], '2026-10-01')).toBeNull()
  })

  it('clamps keyboard focus movement inside the grid', () => {
    expect(moveFocusIndex(0, -1, 31)).toBe(0)
    expect(moveFocusIndex(0, 1, 31)).toBe(1)
    expect(moveFocusIndex(30, 7, 31)).toBe(30)
    expect(moveFocusIndex(5, -7, 31)).toBe(0)
    expect(moveFocusIndex(3, 1, 0)).toBe(0)
  })

  it('computes Monday-first leading blanks', () => {
    expect(monthLeadingBlanks(2026, 10)).toBe(3)
    expect(monthLeadingBlanks(2026, 11)).toBe(6)
  })

  it('navigates across year boundaries and requires a token', async () => {
    const { store } = createStore()
    await store.load()
    await store.previousMonth()
    expect(store.month.value).toBe(9)
    expect(store.days.value).toHaveLength(30)
    await store.previousMonth()
    await store.previousMonth()
    expect(store.month.value).toBe(7)
    const signedOut = createStore({ token: () => null })
    await signedOut.store.load()
    expect(signedOut.store.error.value).toBe('CALENDAR_SIGNED_OUT')
  })
})

describe('calendar import', () => {
  it('parses valid lines, notes, comments and reports invalid or duplicate dates', () => {
    const parsed = parseOverrideLines(
      '# 2026\n2026-10-01,holiday,国庆\n2026-10-03,workday\n2026-10-01,workday\nbad,holiday\n2026-10-05,party'
    )
    expect(parsed.items).toEqual([
      { date: '2026-10-01', kind: 'holiday', note: '国庆' },
      { date: '2026-10-03', kind: 'workday', note: null }
    ])
    expect(parsed.errors).toBe(3)
  })

  it('imports overrides through the store and reloads the month', async () => {
    const { store } = createStore()
    await store.load()
    const result = await store.importLines('2026-10-01,holiday\n2026-10-03,workday')
    expect(result).toEqual({ imported: 2, errors: 0 })
    expect(store.days.value[0]?.kind).toBe('holiday')
    expect(store.days.value[2]?.kind).toBe('workday')
  })
})
