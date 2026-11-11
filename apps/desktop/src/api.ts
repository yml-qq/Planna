export interface Task {
  id: string
  listId: string
  title: string
  status: 'todo' | 'in_progress' | 'done' | 'archived'
  version: number
}

export interface CreateTaskInput {
  id: string
  listId: string
  title: string
}

export interface LoginResult {
  accessToken: string
  expiresIn: number
}

export interface SyncSummary {
  pushed: number
  duplicates: number
  conflicts: string[]
  rejected: number
  pulled: number
  skippedStale: number
  cursor: string | null
}

export interface SyncInput {
  baseUrl: string
  accessToken: string
  deviceId: string
}

/** The shell boundary: the UI only talks to this interface, never to SQLite or HTTP directly. */
export interface DesktopBridge {
  listTasks(listId: string): Promise<Task[]>
  createTask(input: CreateTaskInput): Promise<Task>
  requestLoginCode(baseUrl: string, email: string, deviceId: string): Promise<string>
  login(baseUrl: string, email: string, code: string, deviceName: string, deviceId: string): Promise<LoginResult>
  restoreSession(baseUrl: string): Promise<LoginResult>
  signOut(): Promise<void>
  runSync(input: SyncInput): Promise<SyncSummary>
}

/** Deterministic in-memory bridge for `vite dev` in a browser and unit tests. */
export class MemoryBridge implements DesktopBridge {
  private readonly tasks = new Map<string, Task>()
  private signedIn = false
  syncCalls = 0

  async listTasks(listId: string): Promise<Task[]> {
    return [...this.tasks.values()]
      .filter((task) => task.listId === listId)
      .sort((left, right) => left.title.localeCompare(right.title))
  }

  async createTask(input: CreateTaskInput): Promise<Task> {
    const title = input.title.trim()
    if (title.length === 0 || title.length > 500) {
      throw new Error('INVALID_TITLE')
    }
    if (this.tasks.has(input.id)) {
      return this.tasks.get(input.id)!
    }
    const task: Task = {
      id: input.id,
      listId: input.listId,
      title,
      status: 'todo',
      version: 1
    }
    this.tasks.set(task.id, task)
    return task
  }

  async requestLoginCode(_baseUrl: string, _email: string, _deviceId: string): Promise<string> {
    return new Date(Date.now() + 10 * 60_000).toISOString()
  }

  async login(
    _baseUrl: string,
    _email: string,
    code: string,
    _deviceName: string,
    _deviceId: string
  ): Promise<LoginResult> {
    if (code !== '123456') {
      throw new Error('INVALID_CODE')
    }
    this.signedIn = true
    return { accessToken: 'memory-access-token', expiresIn: 3600 }
  }

  async restoreSession(_baseUrl: string): Promise<LoginResult> {
    if (!this.signedIn) {
      throw new Error('NO_SESSION')
    }
    return { accessToken: 'memory-access-token', expiresIn: 3600 }
  }

  async signOut(): Promise<void> {
    this.signedIn = false
  }

  async runSync(_input: SyncInput): Promise<SyncSummary> {
    this.syncCalls += 1
    return {
      pushed: 0,
      duplicates: 0,
      conflicts: [],
      rejected: 0,
      pulled: 0,
      skippedStale: 0,
      cursor: null
    }
  }
}

type Invoke = <T>(command: string, args?: Record<string, unknown>) => Promise<T>

function toTask(row: Omit<Task, 'listId'> & { list_id: string }): Task {
  return {
    id: row.id,
    listId: row.list_id,
    title: row.title,
    status: row.status,
    version: row.version
  }
}

interface RawSyncSummary {
  pushed: number
  duplicates: number
  conflicts: string[]
  rejected: number
  pulled: number
  skipped_stale: number
  cursor: string | null
}

/** The Tauri shell exposes the same operations as commands backed by the Rust core. */
export class TauriBridge implements DesktopBridge {
  constructor(private readonly invoke: Invoke) {}

  async listTasks(listId: string): Promise<Task[]> {
    const rows = await this.invoke<Array<Omit<Task, 'listId'> & { list_id: string }>>('list_tasks', {
      listId
    })
    return rows.map(toTask)
  }

  async createTask(input: CreateTaskInput): Promise<Task> {
    const row = await this.invoke<Omit<Task, 'listId'> & { list_id: string }>('create_task', {
      id: input.id,
      listId: input.listId,
      title: input.title
    })
    return toTask(row)
  }

  async requestLoginCode(baseUrl: string, email: string, deviceId: string): Promise<string> {
    return this.invoke<string>('request_login_code', { baseUrl, email, deviceId })
  }

  async login(
    baseUrl: string,
    email: string,
    code: string,
    deviceName: string,
    deviceId: string
  ): Promise<LoginResult> {
    const result = await this.invoke<{ access_token: string; expires_in: number }>('login_with_code', {
      baseUrl,
      email,
      code,
      deviceName,
      deviceId
    })
    return { accessToken: result.access_token, expiresIn: result.expires_in }
  }

  async restoreSession(baseUrl: string): Promise<LoginResult> {
    const result = await this.invoke<{ access_token: string; expires_in: number }>('restore_session', {
      baseUrl
    })
    return { accessToken: result.access_token, expiresIn: result.expires_in }
  }

  async signOut(): Promise<void> {
    await this.invoke<void>('sign_out_local')
  }

  async runSync(input: SyncInput): Promise<SyncSummary> {
    const result = await this.invoke<RawSyncSummary>('run_sync', {
      baseUrl: input.baseUrl,
      accessToken: input.accessToken,
      deviceId: input.deviceId
    })
    return {
      pushed: result.pushed,
      duplicates: result.duplicates,
      conflicts: result.conflicts,
      rejected: result.rejected,
      pulled: result.pulled,
      skippedStale: result.skipped_stale,
      cursor: result.cursor
    }
  }
}

export function createBridge(): DesktopBridge {
  if (typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window) {
    return new TauriBridge(async <T>(command: string, args?: Record<string, unknown>): Promise<T> => {
      const module = await import('@tauri-apps/api/core')
      return module.invoke<T>(command, args)
    })
  }
  return new MemoryBridge()
}
