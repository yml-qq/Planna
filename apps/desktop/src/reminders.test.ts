import { describe, expect, it } from 'vitest'
import type { Habit, HabitLog } from './api'
import { MemoryNotifier } from './notifier'
import {
  dueReminders,
  isReminderTime,
  loadQuietHours,
  loadReminders,
  localTime,
  runReminders,
  withinQuietHours,
  type HabitReminder
} from './reminders'

function habit(id: string, overrides: Partial<Habit> = {}): Habit {
  return {
    id,
    title: `Habit ${id}`,
    scheduleType: 'daily',
    weekdays: null,
    targetQuantity: 2,
    unit: 'pages',
    timeZone: 'Asia/Shanghai',
    pausedAt: null,
    version: 1,
    updatedAt: '',
    ...overrides
  }
}

function log(habitId: string, status: 'done' | 'skipped'): HabitLog {
  return {
    id: `log-${habitId}`,
    habitId,
    logDate: '2026-11-12',
    quantity: 2,
    status,
    version: 1,
    updatedAt: ''
  }
}

const reminders: HabitReminder[] = [
  { habitId: 'a', time: '09:00', enabled: true },
  { habitId: 'b', time: '09:00', enabled: true },
  { habitId: 'c', time: '18:00', enabled: true },
  { habitId: 'd', time: '09:00', enabled: false }
]

describe('reminder helpers', () => {
  it('validates and formats local clock values', () => {
    expect(isReminderTime('09:00')).toBe(true)
    expect(isReminderTime('23:59')).toBe(true)
    expect(isReminderTime('24:00')).toBe(false)
    expect(isReminderTime('9:00')).toBe(false)
    expect(localTime(new Date(2026, 10, 12, 7, 5))).toBe('07:05')
  })

  it('selects enabled reminders whose time matches and are still pending', () => {
    const due = dueReminders([habit('a'), habit('b'), habit('c'), habit('d')], { b: log('b', 'done') }, reminders, '09:00')
    expect(due.map((item) => item.habitId)).toEqual(['a'])
    expect(dueReminders([habit('c')], {}, reminders, '18:00').map((item) => item.habitId)).toEqual(['c'])
  })

  it('skips paused habits and disabled reminders', () => {
    const paused = [habit('a', { pausedAt: '2026-11-12T00:00:00Z' }), habit('d')]
    expect(dueReminders(paused, {}, reminders, '09:00')).toEqual([])
  })

  it('sends each habit at most once per day and isolates notifier failures', async () => {
    const notifier = new MemoryNotifier()
    const sent = new Set<string>()
    const habits = [habit('a'), habit('b')]
    expect(await runReminders(notifier, habits, {}, reminders, '2026-11-12', '09:00', sent)).toBe(2)
    expect(notifier.sent).toHaveLength(2)
    expect(await runReminders(notifier, habits, {}, reminders, '2026-11-12', '09:00', sent)).toBe(0)
    expect(notifier.sent).toHaveLength(2)

    const failing = {
      notify: async () => {
        throw new Error('permission denied')
      }
    }
    const failures = new Set<string>()
    expect(await runReminders(failing, habits, {}, reminders, '2026-11-13', '09:00', failures)).toBe(0)
    expect(failures.size).toBe(2)
  })

  it('parses persisted reminders defensively', () => {
    expect(loadReminders(null)).toEqual([])
    expect(loadReminders('not-json')).toEqual([])
    expect(loadReminders(JSON.stringify([{ habitId: 'a', time: '09:00', enabled: true }, { bad: true }]))).toEqual([
      { habitId: 'a', time: '09:00', enabled: true }
    ])
  })
})

describe('quiet hours', () => {
  it('handles overnight and same-day windows', () => {
    expect(withinQuietHours('23:00', { start: '22:00', end: '07:00' })).toBe(true)
    expect(withinQuietHours('06:59', { start: '22:00', end: '07:00' })).toBe(true)
    expect(withinQuietHours('07:00', { start: '22:00', end: '07:00' })).toBe(false)
    expect(withinQuietHours('12:30', { start: '12:00', end: '13:00' })).toBe(true)
    expect(withinQuietHours('13:00', { start: '12:00', end: '13:00' })).toBe(false)
    expect(withinQuietHours('09:00', { start: '09:00', end: '09:00' })).toBe(false)
  })

  it('suppresses notifications during quiet hours', async () => {
    const notifier = new MemoryNotifier()
    const sent = new Set<string>()
    const count = await runReminders(
      notifier,
      [habit('a')],
      {},
      reminders,
      '2026-11-12',
      '09:00',
      sent,
      { start: '08:00', end: '10:00' }
    )
    expect(count).toBe(0)
    expect(notifier.sent).toHaveLength(0)
    expect(sent.size).toBe(0)
  })

  it('parses quiet hours defensively', () => {
    expect(loadQuietHours(null)).toBeNull()
    expect(loadQuietHours('bad')).toBeNull()
    expect(loadQuietHours(JSON.stringify({ start: '22:00' }))).toBeNull()
    expect(loadQuietHours(JSON.stringify({ start: '22:00', end: '07:00' }))).toEqual({
      start: '22:00',
      end: '07:00'
    })
  })
})
