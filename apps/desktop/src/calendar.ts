import { ref, type Ref } from 'vue'
import type { DesktopBridge, WorkdayDay, WorkdayImportItem } from './api'

export interface CalendarStoreOptions {
  baseUrl: () => string
  token: () => string | null
  now?: () => Date
}

export interface CalendarStore {
  year: Ref<number>
  month: Ref<number>
  days: Ref<WorkdayDay[]>
  loading: Ref<boolean>
  error: Ref<string | null>
  load: () => Promise<void>
  nextMonth: () => Promise<void>
  previousMonth: () => Promise<void>
  toggle: (date: string, current: string) => Promise<void>
  clear: (date: string) => Promise<void>
  importLines: (text: string) => Promise<{ imported: number; errors: number } | null>
}

export interface ParsedOverrides {
  items: WorkdayImportItem[]
  errors: number
}

export function parseOverrideLines(text: string): ParsedOverrides {
  const items: WorkdayImportItem[] = []
  const seen = new Set<string>()
  let errors = 0
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (line === '' || line.startsWith('#')) continue
    const [date, kind, ...notes] = line.split(',').map((part) => part.trim())
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date ?? '') ||
        (kind !== 'holiday' && kind !== 'workday') ||
        seen.has(date!)) {
      errors += 1
      continue
    }
    seen.add(date!)
    items.push({ date: date!, kind, note: notes.length > 0 ? notes.join(',') : null })
  }
  return { items, errors }
}

function errorCode(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error)
  const separator = message.indexOf(':')
  return separator >= 0 ? message.slice(separator + 1) : 'CALENDAR_FAILED'
}

export function monthRange(year: number, month: number): { from: string; to: string } {
  const from = `${year}-${String(month).padStart(2, '0')}-01`
  const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate()
  const to = `${year}-${String(month).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`
  return { from, to }
}

export function monthLeadingBlanks(year: number, month: number): number {
  const weekday = new Date(Date.UTC(year, month - 1, 1)).getUTCDay()
  return (weekday + 6) % 7
}

export function createCalendarStore(
  bridge: DesktopBridge,
  options: CalendarStoreOptions
): CalendarStore {
  const today = (options.now ?? (() => new Date()))()
  const year = ref(today.getUTCFullYear())
  const month = ref(today.getUTCMonth() + 1)
  const days = ref<WorkdayDay[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function load(): Promise<void> {
    const token = options.token()
    if (!token) {
      error.value = 'CALENDAR_SIGNED_OUT'
      return
    }
    loading.value = true
    error.value = null
    try {
      const range = monthRange(year.value, month.value)
      days.value = await bridge.workdayDays(options.baseUrl(), token, range.from, range.to)
    } catch (failure) {
      error.value = errorCode(failure)
    } finally {
      loading.value = false
    }
  }

  async function nextMonth(): Promise<void> {
    month.value += 1
    if (month.value > 12) {
      month.value = 1
      year.value += 1
    }
    await load()
  }

  async function previousMonth(): Promise<void> {
    month.value -= 1
    if (month.value < 1) {
      month.value = 12
      year.value -= 1
    }
    await load()
  }

  async function toggle(date: string, current: string): Promise<void> {
    const token = options.token()
    if (!token) return
    error.value = null
    try {
      await bridge.workdayOverrideSet(
        options.baseUrl(),
        token,
        date,
        current === 'workday' ? 'holiday' : 'workday'
      )
      await load()
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  async function clear(date: string): Promise<void> {
    const token = options.token()
    if (!token) return
    error.value = null
    try {
      await bridge.workdayOverrideClear(options.baseUrl(), token, date)
      await load()
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  async function importLines(
    text: string
  ): Promise<{ imported: number; errors: number } | null> {
    const parsed = parseOverrideLines(text)
    if (parsed.items.length === 0) return { imported: 0, errors: parsed.errors }
    const token = options.token()
    if (!token) {
      error.value = 'CALENDAR_SIGNED_OUT'
      return null
    }
    error.value = null
    try {
      await bridge.workdayImport(options.baseUrl(), token, parsed.items)
      await load()
      return { imported: parsed.items.length, errors: parsed.errors }
    } catch (failure) {
      error.value = errorCode(failure)
      return null
    }
  }

  return {
    year,
    month,
    days,
    loading,
    error,
    load,
    nextMonth,
    previousMonth,
    toggle,
    clear,
    importLines
  }
}
