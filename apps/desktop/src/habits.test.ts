import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge } from './api'
import { addDays, createHabitStore, currentStreak, localDate } from './habits'

describe('localDate', () => {
  it('formats the local calendar date with padding', () => {
    expect(localDate(new Date(2026, 0, 5))).toBe('2026-01-05')
    expect(localDate(new Date(2026, 11, 31))).toBe('2026-12-31')
  })
})

function storeOptions(overrides: Record<string, unknown> = {}) {
  return {
    timeZone: () => 'Asia/Shanghai',
    today: () => '2026-11-12',
    newId: () => 'habit-1',
    ...overrides
  }
}

describe('habit store', () => {
  it('creates a daily habit with target and unit through the bridge', async () => {
    const bridge = new MemoryBridge()
    const store = createHabitStore(bridge, storeOptions())
    const habit = await store.create(' Read ', 20, 'pages')
    expect(habit?.title).toBe('Read')
    expect(habit?.targetQuantity).toBe(20)
    expect(habit?.unit).toBe('pages')
    expect(habit?.scheduleType).toBe('daily')
    expect(store.habits.value).toHaveLength(1)
    expect(store.error.value).toBeNull()
  })

  it('rejects invalid habit input without calling the bridge', async () => {
    const bridge = new MemoryBridge()
    let calls = 0
    const spy: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'createHabit') {
          return async (...args: Parameters<DesktopBridge['createHabit']>) => {
            calls += 1
            return target.createHabit(...args)
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const store = createHabitStore(spy, storeOptions())
    expect(await store.create('   ', 1, 'count')).toBeNull()
    expect(store.error.value).toBe('INVALID_HABIT')
    expect(await store.create('Run', 0, 'km')).toBeNull()
    expect(calls).toBe(0)
  })

  it('refreshes habits with only today\'s logs', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    await bridge.upsertHabitLog({
      habitId: 'habit-1',
      logDate: '2026-11-11',
      quantity: 2,
      status: 'done'
    })
    const today = await bridge.upsertHabitLog({
      habitId: 'habit-1',
      logDate: '2026-11-12',
      quantity: 2,
      status: 'done'
    })
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(store.habits.value).toHaveLength(1)
    expect(store.todayLogs.value['habit-1']?.id).toBe(today.id)
    expect(store.todayLogs.value['habit-1']?.logDate).toBe('2026-11-12')
  })

  it('checks in the habit with its target quantity and updates today\'s log', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(await store.checkIn('habit-1')).toBe(true)
    expect(store.todayLogs.value['habit-1']?.quantity).toBe(2)
    expect(store.todayLogs.value['habit-1']?.status).toBe('done')

    const again = await store.checkIn('habit-1')
    expect(again).toBe(true)
    expect(store.todayLogs.value['habit-1']?.version).toBe(2)
  })

  it('reports check-in failures and unknown habits', async () => {
    const failing: DesktopBridge = {
      listTasks: async () => [],
      createTask: async () => {
        throw new Error('offline')
      },
      requestLoginCode: async () => '',
      login: async () => ({ accessToken: '', expiresIn: 0 }),
      restoreSession: async () => ({ accessToken: '', expiresIn: 0 }),
      signOut: async () => {},
      runSync: async () => ({
        pushed: 0,
        duplicates: 0,
        conflicts: [],
        rejected: 0,
        pulled: 0,
        skippedStale: 0,
        cursor: null
      }),
      listHabits: async () => [
        {
          id: 'habit-1',
          title: 'Read',
          scheduleType: 'daily',
          weekdays: null,
          targetQuantity: 1,
          unit: 'count',
          timeZone: 'Asia/Shanghai',
          pausedAt: null,
          version: 1,
          updatedAt: ''
        }
      ],
      createHabit: async () => {
        throw new Error('offline')
      },
      listHabitLogs: async () => [],
      upsertHabitLog: async () => {
        throw new Error('offline')
      },
      updateHabit: async () => {
        throw new Error('offline')
      },
      outboxStatus: async () => ({ pending: 0, failed: 0 }),
      outboxIssues: async () => [],
      discardMutation: async () => false,
      listConflicts: async () => [],
      resolveConflict: async () => false,
      exportConflicts: async () => '{"schema_version":"conflict-ledger-v1","conflicts":[]}' 
    }
    const store = createHabitStore(failing, storeOptions())
    await store.refresh()
    expect(await store.checkIn('habit-1')).toBe(false)
    expect(store.error.value).toBe('CHECK_IN_FAILED')
    expect(await store.checkIn('missing')).toBe(false)
    expect(store.error.value).toBe('HABIT_NOT_FOUND')
  })
})

describe('streak helpers', () => {
  it('adds days across month boundaries', () => {
    expect(addDays('2026-11-01', -1)).toBe('2026-10-31')
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01')
  })

  it('counts consecutive qualifying days ending at today', () => {
    const logs = [
      { id: 'a', habitId: 'h', logDate: '2026-11-09', quantity: 1, status: 'done' as const, version: 1, updatedAt: '' },
      { id: 'b', habitId: 'h', logDate: '2026-11-10', quantity: 2, status: 'done' as const, version: 1, updatedAt: '' },
      { id: 'c', habitId: 'h', logDate: '2026-11-11', quantity: 2, status: 'done' as const, version: 1, updatedAt: '' },
      { id: 'd', habitId: 'h', logDate: '2026-11-12', quantity: 2, status: 'done' as const, version: 1, updatedAt: '' }
    ]
    expect(currentStreak(logs, 2, '2026-11-12')).toBe(3)
    expect(currentStreak(logs, 1, '2026-11-12')).toBe(4)
    expect(currentStreak(logs, 3, '2026-11-12')).toBe(0)
  })
})

describe('habit pause and streaks', () => {
  it('computes streaks on refresh from the log window', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    for (const date of ['2026-11-10', '2026-11-11', '2026-11-12']) {
      await bridge.upsertHabitLog({ habitId: 'habit-1', logDate: date, quantity: 2, status: 'done' })
    }
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(store.streaks.value['habit-1']).toBe(3)
    expect(store.logs.value['habit-1']).toHaveLength(3)
  })

  it('toggles pause through the bridge and keeps the habit version', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(await store.togglePause('habit-1')).toBe(true)
    expect(store.habits.value[0]?.pausedAt).not.toBeNull()
    expect(await store.togglePause('habit-1')).toBe(true)
    expect(store.habits.value[0]?.pausedAt).toBeNull()
    expect(store.habits.value[0]?.version).toBe(3)
  })
})

describe('habit editing', () => {
  it('updates title, target and unit through the bridge', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(await store.updateDetails('habit-1', { title: 'Read daily', targetQuantity: 30, unit: 'minutes' })).toBe(true)
    expect(store.habits.value[0]?.title).toBe('Read daily')
    expect(store.habits.value[0]?.targetQuantity).toBe(30)
    expect(store.habits.value[0]?.unit).toBe('minutes')
    expect(store.error.value).toBeNull()
    expect(await store.updateDetails('habit-1', { title: '  ', targetQuantity: 1, unit: 'x' })).toBe(false)
    expect(store.error.value).toBe('INVALID_HABIT')
  })

  it('reports update failures', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Read',
      scheduleType: 'daily',
      targetQuantity: 2,
      unit: 'pages',
      timeZone: 'Asia/Shanghai'
    })
    const failing: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'updateHabit') {
          return async () => {
            throw new Error('offline')
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const store = createHabitStore(failing, storeOptions())
    await store.refresh()
    expect(await store.updateDetails('habit-1', { title: 'X', targetQuantity: 1, unit: 'x' })).toBe(false)
    expect(store.error.value).toBe('HABIT_UPDATE_FAILED')
  })
})

describe('weekly habit editing', () => {
  it('updates schedule and weekdays through the bridge', async () => {
    const bridge = new MemoryBridge()
    await bridge.createHabit({
      id: 'habit-1',
      title: 'Run',
      scheduleType: 'daily',
      targetQuantity: 1,
      unit: 'km',
      timeZone: 'Asia/Shanghai'
    })
    const store = createHabitStore(bridge, storeOptions())
    await store.refresh()
    expect(
      await store.updateDetails('habit-1', {
        title: 'Run',
        scheduleType: 'weekly',
        weekdays: [1, 3, 5],
        targetQuantity: 1,
        unit: 'km'
      })
    ).toBe(true)
    expect(store.habits.value[0]?.scheduleType).toBe('weekly')
    expect(store.habits.value[0]?.weekdays).toEqual([1, 3, 5])
    expect(
      await store.updateDetails('habit-1', {
        title: 'Run',
        scheduleType: 'daily',
        targetQuantity: 1,
        unit: 'km'
      })
    ).toBe(true)
    expect(store.habits.value[0]?.weekdays).toBeNull()
  })
})

describe('conflict ledger export', () => {
  it('exports a versioned empty ledger from the memory bridge', async () => {
    const bridge = new MemoryBridge()
    const parsed = JSON.parse(await bridge.exportConflicts())
    expect(parsed.schema_version).toBe('conflict-ledger-v1')
    expect(parsed.conflicts).toEqual([])
  })
})
