import { describe, expect, it } from 'vitest'
import { createModuleStore, loadVisibility, MODULE_LABELS } from './modules'

describe('module visibility', () => {
  it('defaults to all modules visible', () => {
    expect(MODULE_LABELS).toEqual({ habits: '习惯', focus: '专注', goals: '目标', notes: '笔记', calendar: '日历' })
    expect(loadVisibility(null)).toEqual({ habits: true, focus: true, goals: true, notes: true, calendar: true })
  })

  it('persists hiding and restoring a module', () => {
    const storage = new Map<string, string>()
    const store = createModuleStore({
      read: (key) => storage.get(key) ?? null,
      write: (key, value) => storage.set(key, value)
    })
    store.toggle('notes')
    expect(store.visibility.value.notes).toBe(false)
    expect(JSON.parse(storage.get('planna.modules') ?? '{}')).toMatchObject({ notes: false })

    const reloaded = createModuleStore({
      read: (key) => storage.get(key) ?? null,
      write: (key, value) => storage.set(key, value)
    })
    expect(reloaded.visibility.value.notes).toBe(false)
    reloaded.toggle('notes')
    expect(reloaded.visibility.value.notes).toBe(true)
  })

  it('falls back defensively for invalid or partial payloads', () => {
    expect(loadVisibility('not-json')).toEqual({ habits: true, focus: true, goals: true, notes: true, calendar: true })
    expect(loadVisibility(JSON.stringify({ notes: false, focus: 'no' }))).toEqual({
      habits: true,
      focus: true,
      goals: true,
      notes: false,
      calendar: true
    })
    expect(loadVisibility(JSON.stringify([1, 2]))).toEqual({
      habits: true,
      focus: true,
      goals: true,
      notes: true,
      calendar: true
    })
  })
})
