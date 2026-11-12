import { ref, type Ref } from 'vue'
import type { DesktopBridge, Habit, HabitLog } from './api'

const STREAK_WINDOW_DAYS = 90

export function localDate(now: Date = new Date()): string {
  const year = now.getFullYear()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function addDays(date: string, delta: number): string {
  const [year, month, day] = date.split('-').map((part) => Number.parseInt(part, 10))
  const utc = new Date(Date.UTC(year ?? 1970, (month ?? 1) - 1, (day ?? 1) + delta))
  return utc.toISOString().slice(0, 10)
}

export function currentStreak(logs: HabitLog[], targetQuantity: number, today: string): number {
  const qualifying = new Set(
    logs
      .filter((log) => log.status === 'done' && log.quantity >= targetQuantity && log.logDate <= today)
      .map((log) => log.logDate)
  )
  let streak = 0
  let cursor = today
  while (qualifying.has(cursor)) {
    streak += 1
    cursor = addDays(cursor, -1)
  }
  return streak
}

export interface HabitStoreOptions {
  timeZone: () => string
  today?: () => string
  newId?: () => string
}

export interface HabitStore {
  habits: Ref<Habit[]>
  todayLogs: Ref<Record<string, HabitLog>>
  logs: Ref<Record<string, HabitLog[]>>
  streaks: Ref<Record<string, number>>
  loading: Ref<boolean>
  error: Ref<string | null>
  refresh: () => Promise<void>
  create: (title: string, targetQuantity: number, unit: string) => Promise<Habit | null>
  updateDetails: (
    habitId: string,
    patch: { title: string; targetQuantity: number; unit: string }
  ) => Promise<boolean>
  togglePause: (habitId: string) => Promise<boolean>
  checkIn: (habitId: string) => Promise<boolean>
}

export function createHabitStore(bridge: DesktopBridge, options: HabitStoreOptions): HabitStore {
  const habits = ref<Habit[]>([])
  const todayLogs = ref<Record<string, HabitLog>>({})
  const logs = ref<Record<string, HabitLog[]>>({})
  const streaks = ref<Record<string, number>>({})
  const loading = ref(false)
  const error = ref<string | null>(null)
  const today = options.today ?? (() => localDate())
  const newId = options.newId ?? (() => crypto.randomUUID())

  function applyLogs(habitId: string, rows: HabitLog[], day: string): void {
    logs.value = { ...logs.value, [habitId]: rows }
    const todayLog = rows.find((log) => log.logDate === day)
    const nextTodayLogs = { ...todayLogs.value }
    if (todayLog) nextTodayLogs[habitId] = todayLog
    else delete nextTodayLogs[habitId]
    todayLogs.value = nextTodayLogs
    const habit = habits.value.find((candidate) => candidate.id === habitId)
    if (habit) {
      streaks.value = {
        ...streaks.value,
        [habitId]: currentStreak(rows, habit.targetQuantity, day)
      }
    }
  }

  async function refresh(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      habits.value = await bridge.listHabits()
      const day = today()
      const from = addDays(day, -(STREAK_WINDOW_DAYS - 1))
      const nextLogs: Record<string, HabitLog[]> = {}
      const nextTodayLogs: Record<string, HabitLog> = {}
      const nextStreaks: Record<string, number> = {}
      for (const habit of habits.value) {
        const rows = await bridge.listHabitLogs(habit.id, from, day)
        nextLogs[habit.id] = rows
        const todayLog = rows.find((log) => log.logDate === day)
        if (todayLog) nextTodayLogs[habit.id] = todayLog
        nextStreaks[habit.id] = currentStreak(rows, habit.targetQuantity, day)
      }
      logs.value = nextLogs
      todayLogs.value = nextTodayLogs
      streaks.value = nextStreaks
    } catch {
      error.value = 'HABITS_LOAD_FAILED'
    } finally {
      loading.value = false
    }
  }

  async function create(title: string, targetQuantity: number, unit: string): Promise<Habit | null> {
    const trimmed = title.trim()
    if (trimmed.length === 0 || trimmed.length > 200 || targetQuantity < 1 || targetQuantity > 1000) {
      error.value = 'INVALID_HABIT'
      return null
    }
    loading.value = true
    error.value = null
    try {
      const habit = await bridge.createHabit({
        id: newId(),
        title: trimmed,
        scheduleType: 'daily',
        targetQuantity,
        unit: unit.trim() || 'count',
        timeZone: options.timeZone()
      })
      habits.value = [habit, ...habits.value.filter((existing) => existing.id !== habit.id)].sort(
        (left, right) => left.title.localeCompare(right.title)
      )
      logs.value = { ...logs.value, [habit.id]: [] }
      streaks.value = { ...streaks.value, [habit.id]: 0 }
      return habit
    } catch {
      error.value = 'HABIT_CREATE_FAILED'
      return null
    } finally {
      loading.value = false
    }
  }

  async function updateDetails(
    habitId: string,
    patch: { title: string; targetQuantity: number; unit: string }
  ): Promise<boolean> {
    const habit = habits.value.find((candidate) => candidate.id === habitId)
    if (!habit) {
      error.value = 'HABIT_NOT_FOUND'
      return false
    }
    const title = patch.title.trim()
    if (title.length === 0 || title.length > 200 || patch.targetQuantity < 1 || patch.targetQuantity > 1000) {
      error.value = 'INVALID_HABIT'
      return false
    }
    loading.value = true
    error.value = null
    try {
      const updated = await bridge.updateHabit(habitId, {
        title,
        targetQuantity: patch.targetQuantity,
        unit: patch.unit.trim() || habit.unit
      })
      habits.value = habits.value.map((candidate) => (candidate.id === habitId ? updated : candidate))
      applyLogs(habitId, logs.value[habitId] ?? [], today())
      return true
    } catch {
      error.value = 'HABIT_UPDATE_FAILED'
      return false
    } finally {
      loading.value = false
    }
  }

  async function togglePause(habitId: string): Promise<boolean> {
    const habit = habits.value.find((candidate) => candidate.id === habitId)
    if (!habit) {
      error.value = 'HABIT_NOT_FOUND'
      return false
    }
    loading.value = true
    error.value = null
    try {
      const updated = await bridge.updateHabit(habitId, { paused: habit.pausedAt === null })
      habits.value = habits.value.map((candidate) => (candidate.id === habitId ? updated : candidate))
      return true
    } catch {
      error.value = 'HABIT_PAUSE_FAILED'
      return false
    } finally {
      loading.value = false
    }
  }

  async function checkIn(habitId: string): Promise<boolean> {
    const habit = habits.value.find((candidate) => candidate.id === habitId)
    if (!habit) {
      error.value = 'HABIT_NOT_FOUND'
      return false
    }
    loading.value = true
    error.value = null
    try {
      const log = await bridge.upsertHabitLog({
        habitId,
        logDate: today(),
        quantity: habit.targetQuantity,
        status: 'done'
      })
      const rows = [...(logs.value[habitId] ?? []).filter((entry) => entry.logDate !== log.logDate), log].sort(
        (left, right) => left.logDate.localeCompare(right.logDate)
      )
      applyLogs(habitId, rows, today())
      return true
    } catch {
      error.value = 'CHECK_IN_FAILED'
      return false
    } finally {
      loading.value = false
    }
  }

  return {
    habits,
    todayLogs,
    logs,
    streaks,
    loading,
    error,
    refresh,
    create,
    updateDetails,
    togglePause,
    checkIn
  }
}
