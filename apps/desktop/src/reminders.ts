import type { Habit } from './api'
import { dateInTimeZone, timeInTimeZone } from './habits'

export interface HabitReminder {
  habitId: string
  time: string
  enabled: boolean
}

export interface QuietHours {
  start: string
  end: string
}

export function withinQuietHours(time: string, quiet: QuietHours): boolean {
  if (!isReminderTime(quiet.start) || !isReminderTime(quiet.end) || quiet.start === quiet.end) {
    return false
  }
  if (quiet.start < quiet.end) return time >= quiet.start && time < quiet.end
  return time >= quiet.start || time < quiet.end
}

export function loadQuietHours(raw: string | null): QuietHours | null {
  if (!raw) return null
  try {
    const value: unknown = JSON.parse(raw)
    if (typeof value !== 'object' || value === null) return null
    const record = value as Record<string, unknown>
    if (typeof record.start !== 'string' || typeof record.end !== 'string') return null
    if (!isReminderTime(record.start) || !isReminderTime(record.end)) return null
    return { start: record.start, end: record.end }
  } catch {
    return null
  }
}

export interface Notifier {
  notify(title: string, body: string): Promise<void>
}

export function isReminderTime(value: string): boolean {
  return /^([01]\d|2[0-3]):[0-5]\d$/.test(value)
}

export function localTime(now: Date = new Date()): string {
  const hours = String(now.getHours()).padStart(2, '0')
  const minutes = String(now.getMinutes()).padStart(2, '0')
  return `${hours}:${minutes}`
}

/** Reminders due at `now` in each habit's own time zone and still missing today's check-in. */
export function dueReminders(
  habits: Habit[],
  reminders: HabitReminder[],
  now: Date,
  hasDone: (habitId: string, date: string) => boolean
): HabitReminder[] {
  const habitsById = new Map(habits.map((habit) => [habit.id, habit]))
  return reminders.filter((reminder) => {
    if (!reminder.enabled) return false
    const habit = habitsById.get(reminder.habitId)
    if (!habit || habit.pausedAt !== null) return false
    if (reminder.time !== timeInTimeZone(habit.timeZone, now)) return false
    return !hasDone(habit.id, dateInTimeZone(habit.timeZone, now))
  })
}

/** Sends at most one notification per habit and date; failures are isolated per habit. */
export async function runReminders(
  notifier: Notifier,
  habits: Habit[],
  reminders: HabitReminder[],
  now: Date,
  sent: Set<string>,
  quiet: QuietHours | null = null,
  hasDone: (habitId: string, date: string) => boolean = () => false
): Promise<number> {
  if (quiet && withinQuietHours(localTime(now), quiet)) return 0
  let count = 0
  for (const reminder of dueReminders(habits, reminders, now, hasDone)) {
    const habit = habits.find((candidate) => candidate.id === reminder.habitId)
    if (!habit) continue
    const key = `${habit.id}:${dateInTimeZone(habit.timeZone, now)}`
    if (sent.has(key)) continue
    sent.add(key)
    try {
      await notifier.notify(
        '习惯打卡提醒',
        `${habit.title}：目标 ${habit.targetQuantity} ${habit.unit}`
      )
      count += 1
    } catch {
      // Keep the day marked so a broken notification channel cannot loop forever.
    }
  }
  return count
}

export function loadReminders(raw: string | null): HabitReminder[] {
  if (!raw) return []
  try {
    const value: unknown = JSON.parse(raw)
    if (!Array.isArray(value)) return []
    return value.flatMap((entry) => {
      if (typeof entry !== 'object' || entry === null) return []
      const record = entry as Record<string, unknown>
      if (
        typeof record.habitId !== 'string' ||
        typeof record.time !== 'string' ||
        !isReminderTime(record.time) ||
        typeof record.enabled !== 'boolean'
      ) {
        return []
      }
      return [{ habitId: record.habitId, time: record.time, enabled: record.enabled }]
    })
  } catch {
    return []
  }
}
