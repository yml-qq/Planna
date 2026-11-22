import { ref, type Ref } from 'vue'
import type { DesktopBridge, FocusSession, FocusState, FocusStats } from './api'

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
  notify?: (title: string, body: string) => Promise<void>
  restSeconds?: () => number
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
  driftSeconds: Ref<number>
  stats: Ref<FocusStats | null>
  recent: Ref<FocusSession[]>
  taskHistory: Ref<Record<string, FocusSession[]>>
  taskCursors: Ref<Record<string, string | null>>
  restHint: Ref<string | null>
  restRemaining: Ref<number>
  restEndsAt: Ref<number | null>
  loading: Ref<boolean>
  error: Ref<string | null>
  start: (plannedSeconds: number, taskId?: string | null) => Promise<boolean>
  pause: () => Promise<boolean>
  resume: () => Promise<boolean>
  complete: (actualSeconds?: number) => Promise<boolean>
  abandon: () => Promise<boolean>
  recover: () => Promise<void>
  loadStats: (from: string, to: string, timeZone: string) => Promise<void>
  loadRecent: (limit?: number) => Promise<void>
  loadTaskHistory: (taskId: string, limit?: number) => Promise<void>
  loadMoreTaskHistory: (taskId: string) => Promise<void>
  saveReport: (filename: string, content: string) => Promise<string | null>
  skipRest: () => void
  tick: () => void
  tickRest: () => void
}

export function createFocusStore(bridge: DesktopBridge, options: FocusStoreOptions): FocusStore {
  const session = ref<FocusSession | null>(null)
  const snapshot = ref<FocusSnapshot | null>(
    loadSnapshot(options.read?.(SNAPSHOT_KEY) ?? null)
  )
  const seconds = ref(snapshot.value ? elapsedSeconds(snapshot.value, options.now?.() ?? Date.now()) : 0)
  const loading = ref(false)
  const error = ref<string | null>(null)
  const driftSeconds = ref(0)
  const stats = ref<FocusStats | null>(null)
  const recent = ref<FocusSession[]>([])
  const taskHistory = ref<Record<string, FocusSession[]>>({})
  const taskCursors = ref<Record<string, string | null>>({})
  const restHint = ref<string | null>(null)
  const restRemaining = ref(0)
  const restEndsAt = ref<number | null>(null)
  let restNotified = false
  let lastTickAt = 0
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
    if (!snapshot.value) return
    const current = now()
    if (
      snapshot.value.state === 'active' &&
      snapshot.value.resumedAt !== null &&
      lastTickAt > 0 &&
      current - lastTickAt > 60_000
    ) {
      driftSeconds.value = Math.floor((current - lastTickAt) / 1000)
    }
    lastTickAt = current
    seconds.value = elapsedSeconds(snapshot.value, current)
  }

  function requireToken(): string | null {
    const token = options.token()
    if (!token) {
      error.value = 'FOCUS_SIGNED_OUT'
      return null
    }
    return token
  }

  async function start(plannedSeconds: number, taskId: string | null = null): Promise<boolean> {
    const token = requireToken()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      const created = await bridge.focusStart(options.baseUrl(), token, plannedSeconds, taskId)
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
      driftSeconds.value = 0
      restHint.value = null
      lastTickAt = now()
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
        driftSeconds.value = 0
        if (nextState === 'completed') {
          const restSeconds = Math.max(0, Math.floor(options.restSeconds?.() ?? 300))
          if (restSeconds === 0) {
            restHint.value = `本次专注 ${formatDuration(actual)}。`
          } else {
            const restMinutes = Math.max(1, Math.round(restSeconds / 60))
            restHint.value = `本次专注 ${formatDuration(actual)}，建议休息 ${restMinutes} 分钟。`
            restEndsAt.value = now() + restSeconds * 1000
            restRemaining.value = restSeconds
            restNotified = false
          }
        }
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

  async function completeWith(actualSeconds?: number): Promise<boolean> {
    const current = snapshot.value
    if (actualSeconds !== undefined && current !== null) {
      if (!Number.isFinite(actualSeconds) || actualSeconds < 0 || actualSeconds > 86400) {
        error.value = 'INVALID_FOCUS_ACTUAL'
        return false
      }
      seconds.value = Math.floor(actualSeconds)
      snapshot.value = { ...current, accumulatedSeconds: Math.floor(actualSeconds), resumedAt: null, state: 'paused' }
    }
    return transition('completed')
  }

  function skipRest(): void {
    restEndsAt.value = null
    restRemaining.value = 0
    restHint.value = null
    restNotified = false
  }

  function tickRest(): void {
    if (restEndsAt.value === null) return
    const remaining = Math.max(0, Math.ceil((restEndsAt.value - now()) / 1000))
    restRemaining.value = remaining
    if (remaining > 0 || restNotified) return
    restNotified = true
    restHint.value = '休息结束，可以开始下一段专注。'
    void options
      .notify?.('休息结束', '可以开始下一段专注了。')
      .catch(() => undefined)
    restEndsAt.value = null
  }

  async function loadRecent(limit = 20): Promise<void> {
    const token = requireToken()
    if (!token) return
    try {
      recent.value = await bridge.focusList(options.baseUrl(), token, limit)
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  async function loadTaskHistory(taskId: string, limit = 50): Promise<void> {
    const token = requireToken()
    if (!token) return
    try {
      const page = await bridge.focusPage(options.baseUrl(), token, { limit, taskId })
      taskHistory.value = { ...taskHistory.value, [taskId]: page.items }
      taskCursors.value = { ...taskCursors.value, [taskId]: page.nextCursor }
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  async function loadMoreTaskHistory(taskId: string): Promise<void> {
    const cursor = taskCursors.value[taskId]
    if (!cursor) return
    const token = requireToken()
    if (!token) return
    try {
      const page = await bridge.focusPage(options.baseUrl(), token, { limit: 50, taskId, cursor })
      taskHistory.value = {
        ...taskHistory.value,
        [taskId]: [...(taskHistory.value[taskId] ?? []), ...page.items]
      }
      taskCursors.value = { ...taskCursors.value, [taskId]: page.nextCursor }
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  async function saveReport(filename: string, content: string): Promise<string | null> {
    error.value = null
    try {
      return await bridge.saveFocusReport(filename, content)
    } catch (failure) {
      error.value = errorCode(failure)
      return null
    }
  }

  async function loadStats(from: string, to: string, timeZone: string): Promise<void> {
    const token = requireToken()
    if (!token) return
    try {
      stats.value = await bridge.focusStats(options.baseUrl(), token, from, to, timeZone)
    } catch (failure) {
      error.value = errorCode(failure)
    }
  }

  return {
    session,
    snapshot,
    seconds,
    driftSeconds,
    stats,
    recent,
    taskHistory,
    taskCursors,
    restHint,
    restRemaining,
    restEndsAt,
    loading,
    error,
    start,
    pause: () => transition('paused'),
    resume: () => transition('active'),
    complete: (actualSeconds?: number) => completeWith(actualSeconds),
    abandon: () => transition('abandoned'),
    recover,
    loadStats,
    loadRecent,
    loadTaskHistory,
    loadMoreTaskHistory,
    saveReport,
    skipRest,
    tick,
    tickRest
  }
}
