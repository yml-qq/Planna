import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge, type LoginResult, type SyncSummary } from './api'
import { createSessionStore, type TimerHandle } from './session'

interface Scheduled {
  callback: () => void
  delay: number
  cancelled: boolean
}

function fakeScheduler(): { scheduled: Scheduled[]; schedule: (callback: () => void, delay: number) => TimerHandle } {
  const scheduled: Scheduled[] = []
  return {
    scheduled,
    schedule: (callback, delay) => {
      const handle: Scheduled = { callback, delay, cancelled: false }
      scheduled.push(handle)
      return { cancel: () => (handle.cancelled = true) }
    }
  }
}

function bridge(overrides: Partial<DesktopBridge> = {}): DesktopBridge {
  const base = new MemoryBridge()
  return new Proxy(base as DesktopBridge, {
    get(target, property, receiver) {
      if (property in overrides) {
        return overrides[property as keyof DesktopBridge]
      }
      return Reflect.get(target, property, receiver)
    }
  }) as DesktopBridge
}

const sessionOptions = (scheduler: ReturnType<typeof fakeScheduler>) => ({
  baseUrl: () => 'http://api.test',
  deviceId: 'device-1',
  schedule: scheduler.schedule
})

describe('session store', () => {
  it('requests a login code only for a plausible email', async () => {
    const calls: string[] = []
    const store = createSessionStore(
      bridge({
        requestLoginCode: async (_baseUrl, email) => {
          calls.push(email)
          return '2026-11-12T00:10:00Z'
        }
      }),
      sessionOptions(fakeScheduler())
    )
    expect(await store.requestCode('not-an-email')).toBe(false)
    expect(store.error.value).toBe('INVALID_EMAIL')
    expect(calls).toEqual([])
    expect(await store.requestCode(' user@example.test ')).toBe(true)
    expect(calls).toEqual(['user@example.test'])
    expect(store.requested.value).toBe(true)
  })

  it('signs in, keeps the token in memory and schedules an early refresh', async () => {
    const scheduler = fakeScheduler()
    const store = createSessionStore(bridge(), sessionOptions(scheduler))
    expect(await store.signIn('user@example.test', '12ab')).toBe(false)
    expect(store.error.value).toBe('INVALID_CREDENTIALS')
    expect(await store.signIn('user@example.test', '123456')).toBe(true)
    expect(store.status.value).toBe('signed_in')
    expect(store.token.value).toBe('memory-access-token')
    expect(scheduler.scheduled).toHaveLength(1)
    expect(scheduler.scheduled[0]!.delay).toBe(3_540_000)
  })

  it('refreshes the access token through the keychain path when the timer fires', async () => {
    let restores = 0
    const scheduler = fakeScheduler()
    const store = createSessionStore(
      bridge({
        restoreSession: async (): Promise<LoginResult> => {
          restores += 1
          return { accessToken: 'rotated-token', expiresIn: 1800 }
        }
      }),
      sessionOptions(scheduler)
    )
    await store.signIn('user@example.test', '123456')
    scheduler.scheduled[0]!.callback()
    await Promise.resolve()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(restores).toBe(1)
    expect(store.token.value).toBe('rotated-token')
  })

  it('signs out locally and cancels the refresh timer', async () => {
    const scheduler = fakeScheduler()
    const store = createSessionStore(bridge(), sessionOptions(scheduler))
    await store.signIn('user@example.test', '123456')
    await store.signOut()
    expect(store.status.value).toBe('signed_out')
    expect(store.token.value).toBeNull()
    expect(scheduler.scheduled[0]!.cancelled).toBe(true)
  })

  it('restores an existing session at startup and falls back to signed out', async () => {
    const scheduler = fakeScheduler()
    const restoring = createSessionStore(
      bridge({
        restoreSession: async () => ({ accessToken: 'restored-token', expiresIn: 600 })
      }),
      sessionOptions(scheduler)
    )
    expect(await restoring.restore()).toBe(true)
    expect(restoring.status.value).toBe('signed_in')
    expect(restoring.token.value).toBe('restored-token')

    const failing = createSessionStore(
      bridge({
        restoreSession: async () => {
          throw new Error('NO_SESSION')
        }
      }),
      sessionOptions(scheduler)
    )
    expect(await failing.restore()).toBe(false)
    expect(failing.status.value).toBe('signed_out')
    expect(failing.token.value).toBeNull()
  })

  it('syncs only with a token and reports summary or failure', async () => {
    const summary: SyncSummary = {
      pushed: 1,
      duplicates: 0,
      conflicts: [],
      rejected: 0,
      pulled: 2,
      skippedStale: 0,
      cursor: 'cursor-1'
    }
    const store = createSessionStore(
      bridge({ runSync: async () => summary }),
      sessionOptions(fakeScheduler())
    )
    expect(await store.sync()).toBe(false)
    expect(store.error.value).toBe('NOT_SIGNED_IN')
    await store.signIn('user@example.test', '123456')
    expect(await store.sync()).toBe(true)
    expect(store.lastSync.value).toEqual(summary)

    const failing = createSessionStore(
      bridge({
        runSync: async () => {
          throw new Error('offline')
        }
      }),
      sessionOptions(fakeScheduler())
    )
    await failing.signIn('user@example.test', '123456')
    expect(await failing.sync()).toBe(false)
    expect(failing.error.value).toBe('SYNC_FAILED')
  })
})
