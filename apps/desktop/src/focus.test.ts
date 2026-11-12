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
