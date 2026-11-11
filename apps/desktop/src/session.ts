import { ref, type Ref } from 'vue'
import type { DesktopBridge, SyncSummary } from './api'

export type SessionStatus = 'signed_out' | 'signed_in'

export interface TimerHandle {
  cancel: () => void
}

export interface SessionOptions {
  baseUrl: () => string
  deviceId: string
  autoRefresh?: boolean
  schedule?: (callback: () => void, delayMs: number) => TimerHandle
}

export interface SessionStore {
  status: Ref<SessionStatus>
  token: Ref<string | null>
  busy: Ref<boolean>
  error: Ref<string | null>
  requested: Ref<boolean>
  lastSync: Ref<SyncSummary | null>
  requestCode: (email: string) => Promise<boolean>
  signIn: (email: string, code: string) => Promise<boolean>
  restore: () => Promise<boolean>
  signOut: () => Promise<void>
  sync: () => Promise<boolean>
}

const REFRESH_LEAD_MS = 60_000
const MIN_REFRESH_MS = 30_000

function defaultSchedule(callback: () => void, delayMs: number): TimerHandle {
  const handle = setTimeout(callback, delayMs)
  return { cancel: () => clearTimeout(handle) }
}

export function createSessionStore(bridge: DesktopBridge, options: SessionOptions): SessionStore {
  const status = ref<SessionStatus>('signed_out')
  const token = ref<string | null>(null)
  const busy = ref(false)
  const error = ref<string | null>(null)
  const requested = ref(false)
  const lastSync = ref<SyncSummary | null>(null)
  const autoRefresh = options.autoRefresh ?? true
  const schedule = options.schedule ?? defaultSchedule
  let refreshHandle: TimerHandle | null = null

  function clearRefresh(): void {
    refreshHandle?.cancel()
    refreshHandle = null
  }

  function scheduleRefresh(expiresInSeconds: number): void {
    clearRefresh()
    if (!autoRefresh) return
    const delay = Math.max(MIN_REFRESH_MS, expiresInSeconds * 1000 - REFRESH_LEAD_MS)
    refreshHandle = schedule(() => {
      void restore()
    }, delay)
  }

  function accept(result: { accessToken: string; expiresIn: number }): void {
    token.value = result.accessToken
    status.value = 'signed_in'
    error.value = null
    scheduleRefresh(result.expiresIn)
  }

  async function requestCode(email: string): Promise<boolean> {
    const trimmed = email.trim()
    if (!trimmed.includes('@')) {
      error.value = 'INVALID_EMAIL'
      return false
    }
    busy.value = true
    error.value = null
    try {
      await bridge.requestLoginCode(options.baseUrl(), trimmed, options.deviceId)
      requested.value = true
      return true
    } catch {
      error.value = 'CODE_REQUEST_FAILED'
      return false
    } finally {
      busy.value = false
    }
  }

  async function signIn(email: string, code: string): Promise<boolean> {
    const trimmed = email.trim()
    if (!trimmed.includes('@') || !/^\d{6}$/.test(code)) {
      error.value = 'INVALID_CREDENTIALS'
      return false
    }
    busy.value = true
    error.value = null
    try {
      const result = await bridge.login(options.baseUrl(), trimmed, code, 'Planna Desktop', options.deviceId)
      accept(result)
      return true
    } catch {
      error.value = 'SIGN_IN_FAILED'
      return false
    } finally {
      busy.value = false
    }
  }

  async function restore(): Promise<boolean> {
    busy.value = true
    try {
      const result = await bridge.restoreSession(options.baseUrl())
      accept(result)
      return true
    } catch {
      clearRefresh()
      token.value = null
      status.value = 'signed_out'
      return false
    } finally {
      busy.value = false
    }
  }

  async function signOut(): Promise<void> {
    clearRefresh()
    busy.value = true
    try {
      await bridge.signOut()
    } finally {
      token.value = null
      status.value = 'signed_out'
      lastSync.value = null
      busy.value = false
    }
  }

  async function sync(): Promise<boolean> {
    if (!token.value) {
      error.value = 'NOT_SIGNED_IN'
      return false
    }
    busy.value = true
    error.value = null
    try {
      lastSync.value = await bridge.runSync({
        baseUrl: options.baseUrl(),
        accessToken: token.value,
        deviceId: options.deviceId
      })
      return true
    } catch {
      error.value = 'SYNC_FAILED'
      return false
    } finally {
      busy.value = false
    }
  }

  return {
    status,
    token,
    busy,
    error,
    requested,
    lastSync,
    requestCode,
    signIn,
    restore,
    signOut,
    sync
  }
}

