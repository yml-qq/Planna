import { ref, type Ref } from 'vue'

export type ModuleKey = 'habits' | 'focus' | 'goals' | 'notes' | 'calendar'

export interface ModuleVisibility {
  habits: boolean
  focus: boolean
  goals: boolean
  notes: boolean
  calendar: boolean
}

export const MODULE_LABELS: Record<ModuleKey, string> = {
  habits: '习惯',
  focus: '专注',
  goals: '目标',
  notes: '笔记',
  calendar: '日历'
}

const STORAGE_KEY = 'planna.modules'

const defaults = (): ModuleVisibility => ({ habits: true, focus: true, goals: true, notes: true, calendar: true })

export function loadVisibility(raw: string | null): ModuleVisibility {
  const fallback = defaults()
  if (!raw) return fallback
  try {
    const value: unknown = JSON.parse(raw)
    if (typeof value !== 'object' || value === null) return fallback
    const record = value as Record<string, unknown>
    return {
      habits: typeof record.habits === 'boolean' ? record.habits : fallback.habits,
      focus: typeof record.focus === 'boolean' ? record.focus : fallback.focus,
      goals: typeof record.goals === 'boolean' ? record.goals : fallback.goals,
      notes: typeof record.notes === 'boolean' ? record.notes : fallback.notes,
      calendar: typeof record.calendar === 'boolean' ? record.calendar : fallback.calendar
    }
  } catch {
    return fallback
  }
}

export interface ModuleStoreOptions {
  read?: (key: string) => string | null
  write?: (key: string, value: string) => void
}

export interface ModuleStore {
  visibility: Ref<ModuleVisibility>
  toggle: (key: ModuleKey) => void
}

export function createModuleStore(options: ModuleStoreOptions = {}): ModuleStore {
  const read = options.read ?? ((key: string) => {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  })
  const write = options.write ?? ((key: string, value: string) => {
    try {
      localStorage.setItem(key, value)
    } catch {
      // Storage may be unavailable; visibility still applies in memory.
    }
  })
  const visibility = ref<ModuleVisibility>(loadVisibility(read(STORAGE_KEY)))

  function toggle(key: ModuleKey): void {
    visibility.value = { ...visibility.value, [key]: !visibility.value[key] }
    write(STORAGE_KEY, JSON.stringify(visibility.value))
  }

  return { visibility, toggle }
}
