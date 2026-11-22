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

export interface Note {
  id: string
  title: string
  content: string
  taskId: string | null
  version: number
  createdAt: string
  updatedAt: string
}

export type GoalState = 'active' | 'completed' | 'archived'

export interface Milestone {
  id: string
  goalId: string
  title: string
  dueDate: string | null
  completed: boolean
  position: number
  version: number
  createdAt: string
  updatedAt: string
}

export interface GoalBreakdownSuggestion {
  title: string
  estimateMinutes: number | null
  position: number
}

export interface GoalProgress {
  completedMilestones: number
  totalMilestones: number
  percent: number
}

export interface Goal {
  id: string
  title: string
  description: string
  targetDate: string | null
  status: GoalState
  version: number
  createdAt: string
  updatedAt: string
  progress: GoalProgress
  milestones: Milestone[]
}

export interface FocusPage {
  items: FocusSession[]
  nextCursor: string | null
}

export interface FocusDayStat {
  date: string
  completedSessions: number
  actualSeconds: number
}

export interface FocusStats {
  from: string
  to: string
  timeZone: string
  totalSessions: number
  completedSessions: number
  abandonedSessions: number
  actualSeconds: number
  days: FocusDayStat[]
}

export interface GoalPatch {
  title?: string
  description?: string
  targetDate?: string | null
  status?: GoalState
}

export interface MilestonePatch {
  title?: string
  dueDate?: string | null
  completed?: boolean
  position?: number
}

export type FocusState = 'active' | 'paused' | 'completed' | 'abandoned'

export interface FocusSession {
  id: string
  taskId: string | null
  startedAt: string
  endedAt: string | null
  plannedSeconds: number
  actualSeconds: number
  breakSeconds: number
  state: FocusState
  version: number
  createdAt: string
  updatedAt: string
}

export interface FocusUpdate {
  state: FocusState
  actualSeconds: number
  breakSeconds: number
  endedAt?: string | null
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

export type ConflictStrategy = 'server' | 'retry' | 'overwrite'

export interface ConflictIssue {
  mutationId: string
  entityType: string
  entityId: string
  serverVersion: number
  createdAt: string
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
  listConflicts(): Promise<ConflictIssue[]>
  resolveConflict(mutationId: string, strategy: ConflictStrategy): Promise<boolean>
  exportConflicts(): Promise<string>
  focusList(
    baseUrl: string,
    accessToken: string,
    limit?: number,
    taskId?: string | null
  ): Promise<FocusSession[]>
  focusStart(baseUrl: string, accessToken: string, plannedSeconds: number, taskId?: string | null): Promise<FocusSession>
  focusUpdate(
    baseUrl: string,
    accessToken: string,
    sessionId: string,
    expectedVersion: number,
    update: FocusUpdate
  ): Promise<FocusSession>
  goalList(baseUrl: string, accessToken: string): Promise<Goal[]>
  goalCreate(
    baseUrl: string,
    accessToken: string,
    input: { title: string; description?: string; targetDate?: string | null }
  ): Promise<Goal>
  goalUpdate(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    expectedVersion: number,
    patch: GoalPatch
  ): Promise<Goal>
  goalAddMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    input: { title: string; dueDate?: string | null }
  ): Promise<Milestone>
  goalUpdateMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number,
    patch: MilestonePatch
  ): Promise<Milestone>
  goalBreakdownPreview(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    guidance?: string
  ): Promise<GoalBreakdownSuggestion[]>
  goalDeleteMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number
  ): Promise<void>
  goalDelete(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    expectedVersion: number
  ): Promise<void>
  focusStats(
    baseUrl: string,
    accessToken: string,
    from: string,
    to: string,
    timeZone: string
  ): Promise<FocusStats>
  focusPage(
    baseUrl: string,
    accessToken: string,
    input?: { limit?: number; taskId?: string | null; cursor?: string | null }
  ): Promise<FocusPage>
  noteList(
    baseUrl: string,
    accessToken: string,
    input?: { query?: string; taskId?: string | null; limit?: number }
  ): Promise<Note[]>
  noteCreate(
    baseUrl: string,
    accessToken: string,
    input: { title?: string; content: string; taskId?: string | null }
  ): Promise<Note>
  noteUpdate(
    baseUrl: string,
    accessToken: string,
    noteId: string,
    expectedVersion: number,
    patch: { title?: string; content?: string }
  ): Promise<Note>
  noteDelete(
    baseUrl: string,
    accessToken: string,
    noteId: string,
    expectedVersion: number
  ): Promise<void>
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

  private readonly focusSessions = new Map<string, FocusSession>()

  async focusList(
    _baseUrl: string,
    _accessToken: string,
    limit = 20,
    taskId: string | null = null
  ): Promise<FocusSession[]> {
    return [...this.focusSessions.values()]
      .filter((session) => taskId === null || session.taskId === taskId)
      .sort((left, right) => right.startedAt.localeCompare(left.startedAt))
      .slice(0, limit)
  }

  async focusStart(
    _baseUrl: string,
    _accessToken: string,
    plannedSeconds: number,
    taskId: string | null = null
  ): Promise<FocusSession> {
    if ([...this.focusSessions.values()].some((session) => session.state === 'active' || session.state === 'paused')) {
      throw new Error('409:FOCUS_SESSION_ACTIVE')
    }
    if (plannedSeconds < 60 || plannedSeconds > 86400) throw new Error('422:INVALID_FOCUS_SESSION')
    const now = new Date().toISOString()
    const session: FocusSession = {
      id: crypto.randomUUID(),
      taskId,
      startedAt: now,
      endedAt: null,
      plannedSeconds,
      actualSeconds: 0,
      breakSeconds: 0,
      state: 'active',
      version: 1,
      createdAt: now,
      updatedAt: now
    }
    this.focusSessions.set(session.id, session)
    return session
  }

  async focusUpdate(
    _baseUrl: string,
    _accessToken: string,
    sessionId: string,
    expectedVersion: number,
    update: FocusUpdate
  ): Promise<FocusSession> {
    const session = this.focusSessions.get(sessionId)
    if (!session) throw new Error('404:FOCUS_SESSION_NOT_FOUND')
    if (session.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    if (session.state === 'completed' || session.state === 'abandoned') {
      throw new Error('409:FOCUS_SESSION_FINISHED')
    }
    const allowed: Record<string, FocusState[]> = {
      active: ['paused', 'completed', 'abandoned'],
      paused: ['active', 'completed', 'abandoned']
    }
    if (!allowed[session.state]?.includes(update.state)) throw new Error('422:INVALID_FOCUS_TRANSITION')
    const terminal = update.state === 'completed' || update.state === 'abandoned'
    const updated: FocusSession = {
      ...session,
      state: update.state,
      actualSeconds: update.actualSeconds,
      breakSeconds: update.breakSeconds,
      endedAt: terminal ? update.endedAt ?? new Date().toISOString() : null,
      version: session.version + 1,
      updatedAt: new Date().toISOString()
    }
    this.focusSessions.set(sessionId, updated)
    return updated
  }

  private readonly goals = new Map<string, Goal>()

  private static refreshProgress(goal: Goal): void {
    const completed = goal.milestones.filter((milestone) => milestone.completed).length
    const total = goal.milestones.length
    goal.progress = {
      completedMilestones: completed,
      totalMilestones: total,
      percent: total === 0 ? 0 : Math.floor((completed * 100) / total)
    }
  }

  async goalList(_baseUrl: string, _accessToken: string): Promise<Goal[]> {
    return [...this.goals.values()].map((goal) => ({
      ...goal,
      milestones: [...goal.milestones].sort((left, right) => left.position - right.position)
    }))
  }

  async goalCreate(
    _baseUrl: string,
    _accessToken: string,
    input: { title: string; description?: string; targetDate?: string | null }
  ): Promise<Goal> {
    const title = input.title.trim()
    if (title.length === 0 || title.length > 200) throw new Error('422:INVALID_GOAL')
    const now = new Date().toISOString()
    const goal: Goal = {
      id: crypto.randomUUID(),
      title,
      description: input.description ?? '',
      targetDate: input.targetDate ?? null,
      status: 'active',
      version: 1,
      createdAt: now,
      updatedAt: now,
      progress: { completedMilestones: 0, totalMilestones: 0, percent: 0 },
      milestones: []
    }
    this.goals.set(goal.id, goal)
    return goal
  }

  async goalUpdate(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    expectedVersion: number,
    patch: GoalPatch
  ): Promise<Goal> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    if (goal.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    if (patch.title !== undefined && (patch.title.trim().length === 0 || patch.title.length > 200)) {
      throw new Error('422:INVALID_GOAL')
    }
    goal.title = patch.title?.trim() ?? goal.title
    goal.description = patch.description ?? goal.description
    goal.targetDate = patch.targetDate === undefined ? goal.targetDate : patch.targetDate
    goal.status = patch.status ?? goal.status
    goal.version += 1
    goal.updatedAt = new Date().toISOString()
    return goal
  }

  async goalAddMilestone(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    input: { title: string; dueDate?: string | null }
  ): Promise<Milestone> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    const title = input.title.trim()
    if (title.length === 0 || title.length > 200) throw new Error('422:INVALID_GOAL')
    const now = new Date().toISOString()
    const milestone: Milestone = {
      id: crypto.randomUUID(),
      goalId,
      title,
      dueDate: input.dueDate ?? null,
      completed: false,
      position: goal.milestones.length + 1,
      version: 1,
      createdAt: now,
      updatedAt: now
    }
    goal.milestones.push(milestone)
    MemoryBridge.refreshProgress(goal)
    return milestone
  }

  async goalUpdateMilestone(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number,
    patch: MilestonePatch
  ): Promise<Milestone> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    const milestone = goal.milestones.find((candidate) => candidate.id === milestoneId)
    if (!milestone) throw new Error('404:MILESTONE_NOT_FOUND')
    if (milestone.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    if (patch.title !== undefined && (patch.title.trim().length === 0 || patch.title.length > 200)) {
      throw new Error('422:INVALID_GOAL')
    }
    milestone.title = patch.title?.trim() ?? milestone.title
    milestone.dueDate = patch.dueDate === undefined ? milestone.dueDate : patch.dueDate
    milestone.completed = patch.completed ?? milestone.completed
    milestone.position = patch.position ?? milestone.position
    milestone.version += 1
    milestone.updatedAt = new Date().toISOString()
    MemoryBridge.refreshProgress(goal)
    return milestone
  }

  async focusStats(
    _baseUrl: string,
    _accessToken: string,
    from: string,
    to: string,
    timeZone: string
  ): Promise<FocusStats> {
    const days = new Map<string, FocusDayStat>()
    let totalSessions = 0
    let completedSessions = 0
    let abandonedSessions = 0
    let actualSeconds = 0
    for (const session of this.focusSessions.values()) {
      const date = session.startedAt.slice(0, 10)
      if (date < from || date > to) continue
      totalSessions += 1
      const day = days.get(date) ?? { date, completedSessions: 0, actualSeconds: 0 }
      if (session.state === 'completed') {
        completedSessions += 1
        day.completedSessions += 1
        day.actualSeconds += session.actualSeconds
        actualSeconds += session.actualSeconds
      } else if (session.state === 'abandoned') {
        abandonedSessions += 1
        day.actualSeconds += session.actualSeconds
        actualSeconds += session.actualSeconds
      }
      days.set(date, day)
    }
    return {
      from,
      to,
      timeZone,
      totalSessions,
      completedSessions,
      abandonedSessions,
      actualSeconds,
      days: [...days.values()].sort((left, right) => left.date.localeCompare(right.date))
    }
  }

  private readonly notes = new Map<string, Note>()

  async noteList(
    _baseUrl: string,
    _accessToken: string,
    input: { query?: string; taskId?: string | null; limit?: number } = {}
  ): Promise<Note[]> {
    const query = (input.query ?? '').trim().toLowerCase()
    return [...this.notes.values()]
      .filter((note) => {
        if (input.taskId && note.taskId !== input.taskId) return false
        if (query.length === 0) return true
        return (
          note.title.toLowerCase().includes(query) || note.content.toLowerCase().includes(query)
        )
      })
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt) || left.id.localeCompare(right.id))
      .slice(0, input.limit ?? 50)
  }

  async noteCreate(
    _baseUrl: string,
    _accessToken: string,
    input: { title?: string; content: string; taskId?: string | null }
  ): Promise<Note> {
    const title = (input.title ?? '').trim()
    const content = input.content ?? ''
    if (title.length > 200 || content.length > 20000 || (title.length === 0 && content.trim().length === 0)) {
      throw new Error('422:INVALID_NOTE')
    }
    const now = new Date().toISOString()
    const note: Note = {
      id: crypto.randomUUID(),
      title,
      content,
      taskId: input.taskId ?? null,
      version: 1,
      createdAt: now,
      updatedAt: now
    }
    this.notes.set(note.id, note)
    return note
  }

  async noteUpdate(
    _baseUrl: string,
    _accessToken: string,
    noteId: string,
    expectedVersion: number,
    patch: { title?: string; content?: string }
  ): Promise<Note> {
    const note = this.notes.get(noteId)
    if (!note) throw new Error('404:NOTE_NOT_FOUND')
    if (note.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    const title = patch.title === undefined ? note.title : patch.title.trim()
    const content = patch.content === undefined ? note.content : patch.content
    if (title.length > 200 || content.length > 20000 || (title.length === 0 && content.trim().length === 0)) {
      throw new Error('422:INVALID_NOTE')
    }
    const updated: Note = {
      ...note,
      title,
      content,
      version: note.version + 1,
      updatedAt: new Date().toISOString()
    }
    this.notes.set(noteId, updated)
    return updated
  }

  async noteDelete(
    _baseUrl: string,
    _accessToken: string,
    noteId: string,
    expectedVersion: number
  ): Promise<void> {
    const note = this.notes.get(noteId)
    if (!note) throw new Error('404:NOTE_NOT_FOUND')
    if (note.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    this.notes.delete(noteId)
  }

  async goalBreakdownPreview(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    guidance?: string
  ): Promise<GoalBreakdownSuggestion[]> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    const source = guidance?.trim() || goal.title
    return source
      .split(/[,，、；;。]/)
      .map((part) => part.trim())
      .filter((part) => part.length > 0)
      .slice(0, 5)
      .map((title, index) => ({ title, estimateMinutes: null, position: index + 1 }))
  }

  async goalDeleteMilestone(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number
  ): Promise<void> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    const milestone = goal.milestones.find((candidate) => candidate.id === milestoneId)
    if (!milestone) throw new Error('404:MILESTONE_NOT_FOUND')
    if (milestone.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    goal.milestones = goal.milestones.filter((candidate) => candidate.id !== milestoneId)
    MemoryBridge.refreshProgress(goal)
  }

  async focusPage(
    _baseUrl: string,
    _accessToken: string,
    input: { limit?: number; taskId?: string | null; cursor?: string | null } = {}
  ): Promise<FocusPage> {
    const limit = input.limit ?? 20
    const offset = input.cursor ? Number.parseInt(input.cursor, 10) : 0
    if (!Number.isFinite(offset) || offset < 0) throw new Error('400:INVALID_CURSOR')
    const filtered = [...this.focusSessions.values()]
      .filter((session) => !input.taskId || session.taskId === input.taskId)
      .sort((left, right) => right.startedAt.localeCompare(left.startedAt))
    const items = filtered.slice(offset, offset + limit)
    const nextCursor = offset + limit < filtered.length ? String(offset + limit) : null
    return { items, nextCursor }
  }

  async goalDelete(
    _baseUrl: string,
    _accessToken: string,
    goalId: string,
    expectedVersion: number
  ): Promise<void> {
    const goal = this.goals.get(goalId)
    if (!goal) throw new Error('404:GOAL_NOT_FOUND')
    if (goal.version !== expectedVersion) throw new Error('409:VERSION_CONFLICT')
    this.goals.delete(goalId)
  }

  async exportConflicts(): Promise<string> {
    return JSON.stringify({
      schema_version: 'conflict-ledger-v1',
      exported_at: new Date().toISOString(),
      conflicts: []
    })
  }

  async listConflicts(): Promise<ConflictIssue[]> {
    return []
  }

  async resolveConflict(_mutationId: string, _strategy: ConflictStrategy): Promise<boolean> {
    return false
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

interface RawNote {
  id: string
  title: string
  content: string
  task_id: string | null
  version: number
  created_at: string
  updated_at: string
}

function toNote(row: RawNote): Note {
  return {
    id: row.id,
    title: row.title,
    content: row.content,
    taskId: row.task_id,
    version: row.version,
    createdAt: row.created_at,
    updatedAt: row.updated_at
  }
}

interface RawMilestone {
  id: string
  goal_id: string
  title: string
  due_date: string | null
  completed: boolean
  position: number
  version: number
  created_at: string
  updated_at: string
}

interface RawGoal {
  id: string
  title: string
  description: string
  target_date: string | null
  status: GoalState
  version: number
  created_at: string
  updated_at: string
  progress: { completed_milestones: number; total_milestones: number; percent: number }
  milestones: RawMilestone[]
}

function toMilestone(row: RawMilestone): Milestone {
  return {
    id: row.id,
    goalId: row.goal_id,
    title: row.title,
    dueDate: row.due_date,
    completed: row.completed,
    position: row.position,
    version: row.version,
    createdAt: row.created_at,
    updatedAt: row.updated_at
  }
}

function toGoal(row: RawGoal): Goal {
  return {
    id: row.id,
    title: row.title,
    description: row.description,
    targetDate: row.target_date,
    status: row.status,
    version: row.version,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    progress: {
      completedMilestones: row.progress.completed_milestones,
      totalMilestones: row.progress.total_milestones,
      percent: row.progress.percent
    },
    milestones: row.milestones.map(toMilestone)
  }
}

interface RawFocusSession {
  id: string
  task_id: string | null
  started_at: string
  ended_at: string | null
  planned_seconds: number
  actual_seconds: number
  break_seconds: number
  state: FocusState
  version: number
  created_at: string
  updated_at: string
}

function toFocusSession(row: RawFocusSession): FocusSession {
  return {
    id: row.id,
    taskId: row.task_id,
    startedAt: row.started_at,
    endedAt: row.ended_at,
    plannedSeconds: row.planned_seconds,
    actualSeconds: row.actual_seconds,
    breakSeconds: row.break_seconds,
    state: row.state,
    version: row.version,
    createdAt: row.created_at,
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

  async focusList(
    baseUrl: string,
    accessToken: string,
    limit = 20,
    taskId: string | null = null
  ): Promise<FocusSession[]> {
    const rows = await this.invoke<RawFocusSession[]>('focus_list', {
      baseUrl,
      accessToken,
      limit,
      taskId
    })
    return rows.map(toFocusSession)
  }

  async focusStart(
    baseUrl: string,
    accessToken: string,
    plannedSeconds: number,
    taskId: string | null = null
  ): Promise<FocusSession> {
    const row = await this.invoke<RawFocusSession>('focus_start', {
      baseUrl,
      accessToken,
      taskId,
      plannedSeconds
    })
    return toFocusSession(row)
  }

  async focusUpdate(
    baseUrl: string,
    accessToken: string,
    sessionId: string,
    expectedVersion: number,
    update: FocusUpdate
  ): Promise<FocusSession> {
    const row = await this.invoke<RawFocusSession>('focus_update', {
      baseUrl,
      accessToken,
      sessionId,
      expectedVersion,
      state: update.state,
      actualSeconds: update.actualSeconds,
      breakSeconds: update.breakSeconds,
      endedAt: update.endedAt ?? null
    })
    return toFocusSession(row)
  }

  async goalList(baseUrl: string, accessToken: string): Promise<Goal[]> {
    const rows = await this.invoke<RawGoal[]>('goal_list', { baseUrl, accessToken })
    return rows.map(toGoal)
  }

  async goalCreate(
    baseUrl: string,
    accessToken: string,
    input: { title: string; description?: string; targetDate?: string | null }
  ): Promise<Goal> {
    const row = await this.invoke<RawGoal>('goal_create', {
      baseUrl,
      accessToken,
      title: input.title,
      description: input.description ?? '',
      targetDate: input.targetDate ?? null
    })
    return toGoal(row)
  }

  async goalUpdate(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    expectedVersion: number,
    patch: GoalPatch
  ): Promise<Goal> {
    const row = await this.invoke<RawGoal>('goal_update', {
      baseUrl,
      accessToken,
      goalId,
      expectedVersion,
      patch: {
        title: patch.title ?? null,
        description: patch.description ?? null,
        target_date: patch.targetDate ?? null,
        status: patch.status ?? null
      }
    })
    return toGoal(row)
  }

  async goalAddMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    input: { title: string; dueDate?: string | null }
  ): Promise<Milestone> {
    const row = await this.invoke<RawMilestone>('goal_add_milestone', {
      baseUrl,
      accessToken,
      goalId,
      title: input.title,
      dueDate: input.dueDate ?? null
    })
    return toMilestone(row)
  }

  async goalUpdateMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number,
    patch: MilestonePatch
  ): Promise<Milestone> {
    const row = await this.invoke<RawMilestone>('goal_update_milestone', {
      baseUrl,
      accessToken,
      goalId,
      milestoneId,
      expectedVersion,
      patch: {
        title: patch.title ?? null,
        due_date: patch.dueDate ?? null,
        completed: patch.completed ?? null,
        position: patch.position ?? null
      }
    })
    return toMilestone(row)
  }

  async focusStats(
    baseUrl: string,
    accessToken: string,
    from: string,
    to: string,
    timeZone: string
  ): Promise<FocusStats> {
    const row = await this.invoke<{
      from: string
      to: string
      time_zone: string
      total_sessions: number
      completed_sessions: number
      abandoned_sessions: number
      actual_seconds: number
      days: Array<{ date: string; completed_sessions: number; actual_seconds: number }>
    }>('focus_stats', { baseUrl, accessToken, from, to, timeZone })
    return {
      from: row.from,
      to: row.to,
      timeZone: row.time_zone,
      totalSessions: row.total_sessions,
      completedSessions: row.completed_sessions,
      abandonedSessions: row.abandoned_sessions,
      actualSeconds: row.actual_seconds,
      days: row.days.map((day) => ({
        date: day.date,
        completedSessions: day.completed_sessions,
        actualSeconds: day.actual_seconds
      }))
    }
  }

  async noteList(
    baseUrl: string,
    accessToken: string,
    input: { query?: string; taskId?: string | null; limit?: number } = {}
  ): Promise<Note[]> {
    const rows = await this.invoke<RawNote[]>('note_list', {
      baseUrl,
      accessToken,
      query: input.query ?? null,
      taskId: input.taskId ?? null,
      limit: input.limit ?? 50
    })
    return rows.map(toNote)
  }

  async noteCreate(
    baseUrl: string,
    accessToken: string,
    input: { title?: string; content: string; taskId?: string | null }
  ): Promise<Note> {
    const row = await this.invoke<RawNote>('note_create', {
      baseUrl,
      accessToken,
      title: input.title ?? '',
      content: input.content,
      taskId: input.taskId ?? null
    })
    return toNote(row)
  }

  async noteUpdate(
    baseUrl: string,
    accessToken: string,
    noteId: string,
    expectedVersion: number,
    patch: { title?: string; content?: string }
  ): Promise<Note> {
    const row = await this.invoke<RawNote>('note_update', {
      baseUrl,
      accessToken,
      noteId,
      expectedVersion,
      title: patch.title ?? null,
      content: patch.content ?? null
    })
    return toNote(row)
  }

  async noteDelete(
    baseUrl: string,
    accessToken: string,
    noteId: string,
    expectedVersion: number
  ): Promise<void> {
    await this.invoke<void>('note_delete', { baseUrl, accessToken, noteId, expectedVersion })
  }

  async goalBreakdownPreview(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    guidance?: string
  ): Promise<GoalBreakdownSuggestion[]> {
    const rows = await this.invoke<
      Array<{ title: string; estimate_minutes: number | null; position: number }>
    >('goal_breakdown_preview', {
      baseUrl,
      accessToken,
      goalId,
      guidance: guidance ?? null
    })
    return rows.map((row) => ({
      title: row.title,
      estimateMinutes: row.estimate_minutes,
      position: row.position
    }))
  }

  async goalDeleteMilestone(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    milestoneId: string,
    expectedVersion: number
  ): Promise<void> {
    await this.invoke<void>('goal_delete_milestone', {
      baseUrl,
      accessToken,
      goalId,
      milestoneId,
      expectedVersion
    })
  }

  async focusPage(
    baseUrl: string,
    accessToken: string,
    input: { limit?: number; taskId?: string | null; cursor?: string | null } = {}
  ): Promise<FocusPage> {
    const page = await this.invoke<{ items: RawFocusSession[]; next_cursor: string | null }>(
      'focus_page',
      {
        baseUrl,
        accessToken,
        limit: input.limit ?? 20,
        taskId: input.taskId ?? null,
        cursor: input.cursor ?? null
      }
    )
    return { items: page.items.map(toFocusSession), nextCursor: page.next_cursor }
  }

  async goalDelete(
    baseUrl: string,
    accessToken: string,
    goalId: string,
    expectedVersion: number
  ): Promise<void> {
    await this.invoke<void>('goal_delete', { baseUrl, accessToken, goalId, expectedVersion })
  }

  async exportConflicts(): Promise<string> {
    return this.invoke<string>('export_conflicts')
  }

  async listConflicts(): Promise<ConflictIssue[]> {
    const rows = await this.invoke<
      Array<{
        mutation_id: string
        entity_type: string
        entity_id: string
        server_version: number
        created_at: string
      }>
    >('list_conflicts')
    return rows.map((row) => ({
      mutationId: row.mutation_id,
      entityType: row.entity_type,
      entityId: row.entity_id,
      serverVersion: row.server_version,
      createdAt: row.created_at
    }))
  }

  async resolveConflict(mutationId: string, strategy: ConflictStrategy): Promise<boolean> {
    return this.invoke<boolean>('resolve_conflict', { mutationId, strategy })
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
