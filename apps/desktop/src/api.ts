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

export interface Habit {
  id: string
  title: string
  scheduleType: 'daily' | 'weekly' | 'weekdays'
  weekdays: number[] | null
  targetQuantity: number
  unit: string
  timeZone: string
  pausedAt: string | null
  version: number
  updatedAt: string
}

export interface HabitLog {
  id: string
  habitId: string
  logDate: string
  quantity: number
  status: 'done' | 'skipped'
  version: number
  updatedAt: string
}

export interface CreateHabitInput {
  id: string
  title: string
  scheduleType: 'daily' | 'weekly' | 'weekdays'
  weekdays?: number[]
  targetQuantity: number
  unit: string
  timeZone: string
}

export interface OutboxIssue {
  mutationId: string
  entityType: string
  entityId: string
  operation: string
  attempts: number
  lastError: string | null
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
  signOut(baseUrl: string, accessToken: string | null): Promise<void>
  runSync(input: SyncInput): Promise<SyncSummary>
  listHabits(): Promise<Habit[]>
  createHabit(input: CreateHabitInput): Promise<Habit>
  updateHabit(
    habitId: string,
    patch: {
      title?: string
      scheduleType?: 'daily' | 'weekly' | 'weekdays'
      weekdays?: number[] | null
      targetQuantity?: number
      unit?: string
      paused?: boolean
    }
  ): Promise<Habit>
  listHabitLogs(habitId: string, from: string, to: string): Promise<HabitLog[]>
  outboxStatus(): Promise<{ pending: number; failed: number }>
  outboxIssues(): Promise<OutboxIssue[]>
  discardMutation(mutationId: string): Promise<boolean>
  upsertHabitLog(input: { habitId: string; logDate: string; quantity: number; status: 'done' | 'skipped' }): Promise<HabitLog>
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

  async signOut(_baseUrl: string, _accessToken: string | null): Promise<void> {
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

  private readonly habits = new Map<string, Habit>()
  private readonly habitLogs = new Map<string, HabitLog>()

  async listHabits(): Promise<Habit[]> {
    return [...this.habits.values()].sort((left, right) => left.title.localeCompare(right.title))
  }

  async createHabit(input: CreateHabitInput): Promise<Habit> {
    const title = input.title.trim()
    if (title.length === 0 || title.length > 200) throw new Error('INVALID_HABIT')
    if (this.habits.has(input.id)) return this.habits.get(input.id)!
    const habit: Habit = {
      id: input.id,
      title,
      scheduleType: input.scheduleType,
      weekdays: input.weekdays ?? null,
      targetQuantity: Math.max(1, input.targetQuantity),
      unit: input.unit.trim() || 'count',
      timeZone: input.timeZone,
      pausedAt: null,
      version: 1,
      updatedAt: new Date().toISOString()
    }
    this.habits.set(habit.id, habit)
    return habit
  }

  async updateHabit(
    habitId: string,
    patch: {
      title?: string
      scheduleType?: 'daily' | 'weekly' | 'weekdays'
      weekdays?: number[] | null
      targetQuantity?: number
      unit?: string
      paused?: boolean
    }
  ): Promise<Habit> {
    const habit = this.habits.get(habitId)
    if (!habit) throw new Error('HABIT_NOT_FOUND')
    const updated: Habit = {
      ...habit,
      title: patch.title?.trim() ?? habit.title,
      scheduleType: patch.scheduleType ?? habit.scheduleType,
      weekdays: patch.scheduleType === 'weekly' ? patch.weekdays ?? habit.weekdays : patch.scheduleType ? null : habit.weekdays,
      targetQuantity: patch.targetQuantity ?? habit.targetQuantity,
      unit: patch.unit?.trim() ?? habit.unit,
      pausedAt:
        patch.paused === true
          ? habit.pausedAt ?? new Date().toISOString()
          : patch.paused === false
            ? null
            : habit.pausedAt,
      version: habit.version + 1,
      updatedAt: new Date().toISOString()
    }
    if (updated.title.length === 0 || updated.title.length > 200) throw new Error('INVALID_HABIT')
    this.habits.set(habitId, updated)
    return updated
  }

  async outboxIssues(): Promise<OutboxIssue[]> {
    return []
  }

  async discardMutation(_mutationId: string): Promise<boolean> {
    return false
  }

  async outboxStatus(): Promise<{ pending: number; failed: number }> {
    return { pending: 0, failed: 0 }
  }

  async listHabitLogs(habitId: string, from: string, to: string): Promise<HabitLog[]> {
    return [...this.habitLogs.values()]
      .filter((log) => log.habitId === habitId && log.logDate >= from && log.logDate <= to)
      .sort((left, right) => left.logDate.localeCompare(right.logDate))
  }

  async upsertHabitLog(input: {
    habitId: string
    logDate: string
    quantity: number
    status: 'done' | 'skipped'
  }): Promise<HabitLog> {
    if (!this.habits.has(input.habitId)) throw new Error('HABIT_NOT_FOUND')
    const key = `${input.habitId}:${input.logDate}`
    const existing = this.habitLogs.get(key)
    const log: HabitLog = {
      id: existing?.id ?? crypto.randomUUID(),
      habitId: input.habitId,
      logDate: input.logDate,
      quantity: Math.max(0, input.quantity),
      status: input.status,
      version: (existing?.version ?? 0) + 1,
      updatedAt: new Date().toISOString()
    }
    this.habitLogs.set(key, log)
    return log
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

interface RawHabit {
  id: string
  title: string
  schedule_type: 'daily' | 'weekly' | 'weekdays'
  weekdays: number[] | null
  target_quantity: number
  unit: string
  time_zone: string
  paused_at: string | null
  version: number
  updated_at: string
}

interface RawHabitLog {
  id: string
  habit_id: string
  log_date: string
  quantity: number
  status: 'done' | 'skipped'
  version: number
  updated_at: string
}

function toHabit(row: RawHabit): Habit {
  return {
    id: row.id,
    title: row.title,
    scheduleType: row.schedule_type,
    weekdays: row.weekdays,
    targetQuantity: row.target_quantity,
    unit: row.unit,
    timeZone: row.time_zone,
    pausedAt: row.paused_at,
    version: row.version,
    updatedAt: row.updated_at
  }
}

function toHabitLog(row: RawHabitLog): HabitLog {
  return {
    id: row.id,
    habitId: row.habit_id,
    logDate: row.log_date,
    quantity: row.quantity,
    status: row.status,
    version: row.version,
    updatedAt: row.updated_at
  }
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

  async signOut(baseUrl: string, accessToken: string | null): Promise<void> {
    if (accessToken) {
      await this.invoke<void>('sign_out_server', { baseUrl, accessToken })
    } else {
      await this.invoke<void>('sign_out_local')
    }
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

  async listHabits(): Promise<Habit[]> {
    const rows = await this.invoke<RawHabit[]>('list_habits')
    return rows.map(toHabit)
  }

  async createHabit(input: CreateHabitInput): Promise<Habit> {
    const row = await this.invoke<RawHabit>('create_habit', {
      id: input.id,
      title: input.title,
      scheduleType: input.scheduleType,
      weekdays: input.weekdays ?? null,
      targetQuantity: input.targetQuantity,
      unit: input.unit,
      timeZone: input.timeZone
    })
    return toHabit(row)
  }

  async updateHabit(
    habitId: string,
    patch: {
      title?: string
      scheduleType?: 'daily' | 'weekly' | 'weekdays'
      weekdays?: number[] | null
      targetQuantity?: number
      unit?: string
      paused?: boolean
    }
  ): Promise<Habit> {
    const row = await this.invoke<RawHabit>('update_habit', {
      habitId,
      title: patch.title ?? null,
      scheduleType: patch.scheduleType ?? null,
      weekdays: patch.weekdays ?? null,
      targetQuantity: patch.targetQuantity ?? null,
      unit: patch.unit ?? null,
      paused: patch.paused ?? null
    })
    return toHabit(row)
  }

  async listHabitLogs(habitId: string, from: string, to: string): Promise<HabitLog[]> {
    const rows = await this.invoke<RawHabitLog[]>('list_habit_logs', { habitId, from, to })
    return rows.map(toHabitLog)
  }

  async outboxIssues(): Promise<OutboxIssue[]> {
    const rows = await this.invoke<
      Array<{
        mutation_id: string
        entity_type: string
        entity_id: string
        operation: string
        attempts: number
        last_error: string | null
      }>
    >('outbox_issues')
    return rows.map((row) => ({
      mutationId: row.mutation_id,
      entityType: row.entity_type,
      entityId: row.entity_id,
      operation: row.operation,
      attempts: row.attempts,
      lastError: row.last_error
    }))
  }

  async discardMutation(mutationId: string): Promise<boolean> {
    return this.invoke<boolean>('discard_mutation', { mutationId })
  }

  async outboxStatus(): Promise<{ pending: number; failed: number }> {
    const row = await this.invoke<{ pending: number; failed: number }>('outbox_status')
    return { pending: row.pending, failed: row.failed }
  }

  async upsertHabitLog(input: {
    habitId: string
    logDate: string
    quantity: number
    status: 'done' | 'skipped'
  }): Promise<HabitLog> {
    const row = await this.invoke<RawHabitLog>('upsert_habit_log', {
      habitId: input.habitId,
      logDate: input.logDate,
      quantity: input.quantity,
      status: input.status
    })
    return toHabitLog(row)
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
