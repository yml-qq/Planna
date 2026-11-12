import { ref, type Ref } from 'vue'
import type { DesktopBridge, Habit, HabitLog } from './api'

export function localDate(now: Date = new Date()): string {
  const year = now.getFullYear()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export interface HabitStoreOptions {
  timeZone: () => string
  today?: () => string
  newId?: () => string
}

export interface HabitStore {
  habits: Ref<Habit[]>
  todayLogs: Ref<Record<string, HabitLog>>
  loading: Ref<boolean>
  error: Ref<string | null>
  refresh: () => Promise<void>
  create: (title: string, targetQuantity: number, unit: string) => Promise<Habit | null>
  checkIn: (habitId: string) => Promise<boolean>
}

export function createHabitStore(bridge: DesktopBridge, options: HabitStoreOptions): HabitStore {
  const habits = ref<Habit[]>([])
  const todayLogs = ref<Record<string, HabitLog>>({})
  const loading = ref(false)
  const error = ref<string | null>(null)
  const today = options.today ?? (() => localDate())
  const newId = options.newId ?? (() => crypto.randomUUID())

  async function refresh(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      habits.value = await bridge.listHabits()
      const day = today()
      const logs: Record<string, HabitLog> = {}
      for (const habit of habits.value) {
        const rows = await bridge.listHabitLogs(habit.id, day, day)
        const log = rows[0]
        if (log) logs[habit.id] = log
      }
      todayLogs.value = logs
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
      return habit
    } catch {
      error.value = 'HABIT_CREATE_FAILED'
      return null
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
      todayLogs.value = { ...todayLogs.value, [habitId]: log }
      return true
    } catch {
      error.value = 'CHECK_IN_FAILED'
      return false
    } finally {
      loading.value = false
    }
  }

  return { habits, todayLogs, loading, error, refresh, create, checkIn }
}
