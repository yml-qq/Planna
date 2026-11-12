import { ref, type Ref } from 'vue'
import type { DesktopBridge, FocusSession, FocusState } from './api'

const SNAPSHOT_KEY = 'planna.focus'

export interface FocusSnapshot {
  sessionId: string
  version: number
  state: FocusState
  plannedSeconds: number
  accumulatedSeconds: number
  resumedAt: number | null
}

export interface FocusStoreOptions {
  baseUrl: () => string
  token: () => string | null
  read?: (key: string) => string | null
  write?: (key: string, value: string) => void
  now?: () => number
}

export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const rest = seconds % 60
  const mm = String(minutes).padStart(2, '0')
  const ss = String(rest).padStart(2, '0')
  return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`
}

export function elapsedSeconds(snapshot: FocusSnapshot, nowMs: number): number {
  if (snapshot.state !== 'active' || snapshot.resumedAt === null) {
    return Math.max(0, snapshot.accumulatedSeconds)
  }
  return Math.max(0, snapshot.accumulatedSeconds + Math.floor((nowMs - snapshot.resumedAt) / 1000))
}

export function loadSnapshot(raw: string | null): FocusSnapshot | null {
  if (!raw) return null
  try {
    const value: unknown = JSON.parse(raw)
    if (typeof value !== 'object' || value === null) return null
    const record = value as Record<string, unknown>
    if (
      typeof record.sessionId !== 'string' ||
      typeof record.version !== 'number' ||
      !['active', 'paused', 'completed', 'abandoned'].includes(String(record.state)) ||
      typeof record.plannedSeconds !== 'number' ||
      typeof record.accumulatedSeconds !== 'number' ||
      !(record.resumedAt === null || typeof record.resumedAt === 'number')
    ) {
      return null
    }
    return {
      sessionId: record.sessionId,
      version: record.version,
      state: record.state as FocusState,
      plannedSeconds: record.plannedSeconds,
      accumulatedSeconds: record.accumulatedSeconds,
      resumedAt: record.resumedAt
    }
  } catch {
    return null
  }
}

function errorCode(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error)
  const separator = message.indexOf(':')
  return separator >= 0 ? message.slice(separator + 1) : 'FOCUS_FAILED'
}

export interface FocusStore {
  session: Ref<FocusSession | null>
  snapshot: Ref<FocusSnapshot | null>
  seconds: Ref<number>
  loading: Ref<boolean>
  error: Ref<string | null>
  start: (plannedSeconds: number) => Promise<boolean>
  pause: () => Promise<boolean>
  resume: () => Promise<boolean>
  complete: () => Promise<boolean>
  abandon: () => Promise<boolean>
  recover: () => Promise<void>
  tick: () => void
}

export function createFocusStore(bridge: DesktopBridge, options: FocusStoreOptions): FocusStore {
  const session = ref<FocusSession | null>(null)
  const snapshot = ref<FocusSnapshot | null>(
    loadSnapshot(options.read?.(SNAPSHOT_KEY) ?? null)
  )
  const seconds = ref(snapshot.value ? elapsedSeconds(snapshot.value, options.now?.() ?? Date.now()) : 0)
  const loading = ref(false)
  const error = ref<string | null>(null)
  const now = options.now ?? (() => Date.now())
  const read = options.read ?? (() => null)
  const write =
    options.write ??
    ((key: string, value: string) => {
      try {
        localStorage.setItem(key, value)
      } catch {
        // Storage may be unavailable; the timer still works in memory.
      }
    })

  function persist(next: FocusSnapshot | null): void {
    snapshot.value = next
    write(SNAPSHOT_KEY, next ? JSON.stringify(next) : '')
  }

  function tick(): void {
    if (snapshot.value) seconds.value = elapsedSeconds(snapshot.value, now())
  }

  function requireToken(): string | null {
    const token = options.token()
    if (!token) {
      error.value = 'FOCUS_SIGNED_OUT'
      return null
    }
    return token
  }

  async function start(plannedSeconds: number): Promise<boolean> {
    const token = requireToken()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      const created = await bridge.focusStart(options.baseUrl(), token, plannedSeconds)
      session.value = created
      persist({
        sessionId: created.id,
        version: created.version,
        state: created.state,
        plannedSeconds: created.plannedSeconds,
        accumulatedSeconds: created.actualSeconds,
        resumedAt: now()
      })
      seconds.value = created.actualSeconds
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function transition(nextState: FocusState): Promise<boolean> {
    const current = snapshot.value
    if (!current) {
      error.value = 'FOCUS_IDLE'
      return false
    }
    const token = requireToken()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      const actual = elapsedSeconds(current, now())
      const terminal = nextState === 'completed' || nextState === 'abandoned'
      const updated = await bridge.focusUpdate(
        options.baseUrl(),
        token,
        current.sessionId,
        current.version,
        {
          state: nextState,
          actualSeconds: actual,
          breakSeconds: 0,
          endedAt: terminal ? new Date(now()).toISOString() : null
        }
      )
      session.value = updated
      if (terminal) {
        persist(null)
        seconds.value = actual
      } else {
        persist({
          sessionId: updated.id,
          version: updated.version,
          state: updated.state,
          plannedSeconds: updated.plannedSeconds,
          accumulatedSeconds: actual,
          resumedAt: nextState === 'active' ? now() : null
        })
      }
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function recover(): Promise<void> {
    const token = options.token()
    if (!token) return
    loading.value = true
    error.value = null
    try {
      const sessions = await bridge.focusList(options.baseUrl(), token, 20)
      const open = sessions.find((candidate) => candidate.state === 'active' || candidate.state === 'paused')
      if (!open) {
        session.value = null
        persist(null)
        seconds.value = 0
        return
      }
      session.value = open
      const stored = loadSnapshot(read(SNAPSHOT_KEY))
      if (stored && stored.sessionId === open.id && stored.version === open.version) {
        persist(stored)
      } else {
        persist({
          sessionId: open.id,
          version: open.version,
          state: open.state,
          plannedSeconds: open.plannedSeconds,
          accumulatedSeconds: open.actualSeconds,
          resumedAt: open.state === 'active' ? now() : null
        })
      }
      tick()
    } catch (failure) {
      error.value = errorCode(failure)
    } finally {
      loading.value = false
    }
  }

  return {
    session,
    snapshot,
    seconds,
    loading,
    error,
    start,
    pause: () => transition('paused'),
    resume: () => transition('active'),
    complete: () => transition('completed'),
    abandon: () => transition('abandoned'),
    recover,
    tick
  }
}
