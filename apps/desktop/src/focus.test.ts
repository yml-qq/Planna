import { describe, expect, it } from 'vitest'
import { MemoryBridge } from './api'
import { createFocusStore, elapsedSeconds, formatDuration, loadSnapshot } from './focus'

function createStore(now: () => number, overrides: Record<string, unknown> = {}) {
  const bridge = new MemoryBridge()
  const storage = new Map<string, string>()
  const store = createFocusStore(bridge, {
    baseUrl: () => 'http://127.0.0.1:5080',
    token: () => 'token',
    read: (key) => storage.get(key) ?? null,
    write: (key, value) => storage.set(key, value),
    now,
    ...overrides
  })
  return { store, storage, bridge }
}

describe('focus helpers', () => {
  it('formats durations with optional hours', () => {
    expect(formatDuration(0)).toBe('00:00')
    expect(formatDuration(75)).toBe('01:15')
    expect(formatDuration(3725)).toBe('1:02:05')
    expect(formatDuration(-5)).toBe('00:00')
  })

  it('computes elapsed seconds only while active', () => {
    const snapshot = {
      sessionId: 's',
      version: 1,
      state: 'active' as const,
      plannedSeconds: 1500,
      accumulatedSeconds: 60,
      resumedAt: 1_000_000
    }
    expect(elapsedSeconds(snapshot, 1_000_000 + 30_000)).toBe(90)
    expect(elapsedSeconds({ ...snapshot, state: 'paused', resumedAt: null }, 2_000_000)).toBe(60)
  })

  it('parses stored snapshots defensively', () => {
    expect(loadSnapshot(null)).toBeNull()
    expect(loadSnapshot('bad')).toBeNull()
    const valid = {
      sessionId: 's',
      version: 1,
      state: 'paused',
      plannedSeconds: 1500,
      accumulatedSeconds: 120,
      resumedAt: null
    }
    expect(loadSnapshot(JSON.stringify(valid))).toEqual(valid)
    expect(loadSnapshot(JSON.stringify({ ...valid, state: 'running' }))).toBeNull()
  })
})

describe('focus store', () => {
  it('runs start, pause, resume and complete with versions', async () => {
    let clock = 1_000_000
    const { store, storage } = createStore(() => clock)
    expect(await store.start(1500)).toBe(true)
    expect(store.snapshot.value?.state).toBe('active')
    clock += 30_000
    store.tick()
    expect(store.seconds.value).toBe(30)

    expect(await store.pause()).toBe(true)
    expect(store.snapshot.value?.state).toBe('paused')
    expect(store.snapshot.value?.accumulatedSeconds).toBe(30)
    clock += 60_000
    expect(await store.resume()).toBe(true)
    clock += 15_000
    expect(await store.complete()).toBe(true)
    expect(store.snapshot.value).toBeNull()
    expect(store.session.value?.state).toBe('completed')
    expect(store.session.value?.version).toBe(4)
    expect(store.seconds.value).toBe(45)
    expect(storage.get('planna.focus')).toBe('')
  })

  it('recovers an open session and its stored snapshot', async () => {
    let clock = 2_000_000
    const first = createStore(() => clock)
    await first.store.start(1500)
    clock += 10_000
    const stored = (first.storage.get('planna.focus') ?? '') as string
    expect(loadSnapshot(stored)?.accumulatedSeconds).toBe(0)

    const bridge = new MemoryBridge()
    const started = await bridge.focusStart('', 'token', 1500)
    const storage = new Map<string, string>([
      [
        'planna.focus',
        JSON.stringify({
          sessionId: started.id,
          version: started.version,
          state: 'active',
          plannedSeconds: started.plannedSeconds,
          accumulatedSeconds: 0,
          resumedAt: clock
        })
      ]
    ])
    const store = createFocusStore(bridge, {
      baseUrl: () => '',
      token: () => 'token',
      read: (key) => storage.get(key) ?? null,
      write: (key, value) => storage.set(key, value),
      now: () => clock
    })
    await store.recover()
    expect(store.snapshot.value?.sessionId).toBe(started.id)
    clock += 5_000
    store.tick()
    expect(store.seconds.value).toBe(5)
  })

  it('falls back to server progress when no snapshot matches', async () => {
    const bridge = new MemoryBridge()
    const clock = 3_000_000
    const session = await bridge.focusStart('', 'token', 1500)
    const storage = new Map<string, string>()
    const store = createFocusStore(bridge, {
      baseUrl: () => '',
      token: () => 'token',
      read: (key) => storage.get(key) ?? null,
      write: (key, value) => storage.set(key, value),
      now: () => clock
    })
    await store.recover()
    expect(store.snapshot.value?.sessionId).toBe(session.id)
    expect(store.snapshot.value?.resumedAt).toBe(clock)
  })

  it('links a task and applies an explicit completion duration', async () => {
    const clock = 4_000_000
    const { store, bridge } = createStore(() => clock)
    expect(await store.start(1500, 'task-1')).toBe(true)
    expect(store.session.value?.taskId).toBe('task-1')
    expect(await store.complete(2400)).toBe(true)
    expect(store.session.value?.actualSeconds).toBe(2400)
    expect(await bridge.focusList('', 'token')).toHaveLength(1)
  })

  it('flags background drift when ticks are far apart', async () => {
    let clock = 5_000_000
    const { store } = createStore(() => clock)
    await store.start(1500)
    clock += 120_000
    store.tick()
    expect(store.driftSeconds.value).toBe(120)
    clock += 1_000
    store.tick()
    expect(store.driftSeconds.value).toBe(120)
    expect(store.seconds.value).toBe(121)
  })

  it('rejects invalid completion overrides', async () => {
    const { store } = createStore(() => 6_000_000)
    await store.start(1500)
    expect(await store.complete(-5)).toBe(false)
    expect(store.error.value).toBe('INVALID_FOCUS_ACTUAL')
    expect(store.snapshot.value?.state).toBe('active')
  })

  it('maps open-session conflicts and requires a token', async () => {
    const bridge = new MemoryBridge()
    await bridge.focusStart('', 'token', 1500)
    const store = createFocusStore(bridge, {
      baseUrl: () => '',
      token: () => 'token',
      read: () => null,
      write: () => {},
      now: () => 0
    })
    expect(await store.start(1500)).toBe(false)
    expect(store.error.value).toBe('FOCUS_SESSION_ACTIVE')

    const signedOut = createStore(() => 0, { token: () => null })
    expect(await signedOut.store.start(1500)).toBe(false)
    expect(signedOut.store.error.value).toBe('FOCUS_SIGNED_OUT')
  })
})

describe('focus statistics', () => {
  it('aggregates completed sessions by day and exposes a rest hint', async () => {
    let clock = 7_000_000
    const { store, bridge } = createStore(() => clock)
    await store.start(1500)
    clock += 600_000
    await store.complete()
    expect(store.restHint.value).toContain('建议休息')
    const sessions = await bridge.focusList('', 'token')
    const date = sessions[0]!.startedAt.slice(0, 10)
    await store.loadStats(date, date, 'UTC')
    expect(store.stats.value?.completedSessions).toBe(1)
    expect(store.stats.value?.actualSeconds).toBe(600)
    expect(store.stats.value?.days).toEqual([
      { date, completedSessions: 1, actualSeconds: 600 }
    ])
    expect(await bridge.focusList('', 'token')).toHaveLength(1)
  })

  it('requires a token before loading statistics', async () => {
    const { store } = createStore(() => 0, { token: () => null })
    await store.loadStats('2026-11-01', '2026-11-07', 'UTC')
    expect(store.stats.value).toBeNull()
    expect(store.error.value).toBe('FOCUS_SIGNED_OUT')
  })
})

describe('rest countdown', () => {
  it('counts down after completion and notifies once when rest ends', async () => {
    let clock = 9_000_000
    const notifications: string[] = []
    const { store } = createStore(() => clock, {
      notify: async (title: string) => {
        notifications.push(title)
      }
    })
    await store.start(1500)
    clock += 600_000
    await store.complete()
    expect(store.restEndsAt.value).toBe(clock + 300_000)
    store.tickRest()
    expect(store.restRemaining.value).toBe(300)

    clock += 299_000
    store.tickRest()
    expect(store.restRemaining.value).toBe(1)
    expect(notifications).toHaveLength(0)

    clock += 1_000
    store.tickRest()
    expect(store.restRemaining.value).toBe(0)
    expect(store.restHint.value).toContain('休息结束')
    expect(notifications).toEqual(['休息结束'])
    expect(store.restEndsAt.value).toBeNull()

    store.tickRest()
    expect(notifications).toEqual(['休息结束'])
  })

  it('honours a configured rest duration', async () => {
    let clock = 11_000_000
    const notifications: string[] = []
    const { store } = createStore(() => clock, {
      restSeconds: () => 90,
      notify: async (title: string) => {
        notifications.push(title)
      }
    })
    await store.start(1500)
    clock += 30_000
    await store.complete()
    expect(store.restEndsAt.value).toBe(clock + 90_000)
    store.tickRest()
    expect(store.restRemaining.value).toBe(90)
    clock += 90_000
    store.tickRest()
    expect(store.restRemaining.value).toBe(0)
    expect(notifications).toEqual(['休息结束'])
  })

  it('skips rest and clears the reminder', async () => {
    let clock = 10_000_000
    const { store } = createStore(() => clock)
    await store.start(1500)
    clock += 60_000
    await store.complete()
    expect(store.restEndsAt.value).not.toBeNull()
    store.skipRest()
    expect(store.restEndsAt.value).toBeNull()
    expect(store.restHint.value).toBeNull()
    clock += 300_000
    store.tickRest()
    expect(store.restRemaining.value).toBe(0)
  })
})
