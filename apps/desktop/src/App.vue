<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { createBridge, type ConflictIssue, type ConflictStrategy, type OutboxIssue } from './api'
import { createSessionStore } from './session'
import { createFocusStore, formatDuration } from './focus'
import { createGoalStore, filterGoals } from './goals'
import type { GoalBreakdownSuggestion } from './api'
import { createModuleStore, MODULE_LABELS, type ModuleKey } from './modules'
import { densityClass, navLinks, normalizeTheme, type Theme } from './prefs'
import { attachmentErrorMessage, canPreviewAttachment, createNoteStore, notesForTask } from './notes'
import {
  arrowAction,
  createCalendarStore,
  monthLeadingBlanks,
  moveFocusIndex,
  tabbableDate
} from './calendar'
import { focusReportFilename, focusStatsCsv, focusStatsJson, focusStatsSummary } from './report'
import ConfirmDialog from './ConfirmDialog.vue'
import { addDays, createHabitStore, localDate } from './habits'
import { createNotifier } from './notifier'
import {
  isReminderTime,
  loadQuietHours,
  loadReminders,
  runReminders,
  type HabitReminder,
  type QuietHours
} from './reminders'
import { createTaskStore } from './tasks'

function readBaseUrl(): string {
  try {
    return localStorage.getItem('planna.baseUrl') ?? 'http://127.0.0.1:5080'
  } catch {
    return 'http://127.0.0.1:5080'
  }
}

function readDeviceId(): string {
  try {
    const existing = localStorage.getItem('planna.deviceId')
    if (existing) return existing
    const generated = crypto.randomUUID()
    localStorage.setItem('planna.deviceId', generated)
    return generated
  } catch {
    return crypto.randomUUID()
  }
}

function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // Storage may be unavailable; reminders stay in memory.
  }
}

const bridge = createBridge()
const baseUrl = ref(readBaseUrl())
const deviceId = ref(readDeviceId())
const session = createSessionStore(bridge, {
  baseUrl: () => baseUrl.value,
  deviceId: deviceId.value
})
const tasks = createTaskStore(bridge)
const habits = createHabitStore(bridge, {
  timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'Asia/Shanghai'
})
const focusTaskId = ref('')
const focusMinutes = ref('')
const expandedTask = ref<string | null>(null)
const taskNoteDrafts = ref<Record<string, string>>({})
const editingTaskNote = ref<string | null>(null)
const editTaskNoteTitle = ref('')
const editTaskNoteContent = ref('')
const focusRestMinutes = ref(readStorage('planna.focusRestMinutes') ?? '5')
const focusStatsDays = ref(7)
const modules = createModuleStore()
const notes = createNoteStore(bridge, {
  baseUrl: () => baseUrl.value,
  token: () => session.token.value
})
const calendar = createCalendarStore(bridge, {
  baseUrl: () => baseUrl.value,
  token: () => session.token.value
})
const noteTitle = ref('')
const noteContent = ref('')
const noteTaskId = ref('')
const editingNote = ref<string | null>(null)
const editNoteTitle = ref('')
const editNoteContent = ref('')
const goals = createGoalStore(bridge, {
  baseUrl: () => baseUrl.value,
  token: () => session.token.value
})
const goalTitle = ref('')
const goalTargetDate = ref('')
const showArchivedGoals = ref(false)
const goalGuidance = ref<Record<string, string>>({})
const editingGoal = ref<string | null>(null)
const editGoalTitle = ref('')
const editGoalDescription = ref('')
const editGoalTargetDate = ref('')
const milestoneDrafts = ref<Record<string, string>>({})
const focus = createFocusStore(bridge, {
  baseUrl: () => baseUrl.value,
  token: () => session.token.value,
  notify: async (title, body) => {
    const notifier = await notifierPromise
    await notifier.notify(title, body)
  },
  restSeconds: () => {
    const minutes = Number.parseInt(focusRestMinutes.value, 10)
    return Number.isFinite(minutes) && minutes > 0 ? minutes * 60 : 0
  }
})

const reminders = ref<HabitReminder[]>(loadReminders(readStorage('planna.habitReminders')))
const storedQuiet = loadQuietHours(readStorage('planna.quietHours'))
const quietStart = ref(storedQuiet?.start ?? '')
const quietEnd = ref(storedQuiet?.end ?? '')
const editingHabit = ref<string | null>(null)
const editHabitTitle = ref('')
const editHabitTarget = ref('1')
const editHabitUnit = ref('')
const editHabitSchedule = ref<'daily' | 'weekly' | 'weekdays'>('daily')
const editHabitWeekdays = ref<number[]>([])
const outbox = ref({ pending: 0, failed: 0 })
const issues = ref<OutboxIssue[]>([])
const conflicts = ref<ConflictIssue[]>([])
const ledgerText = ref('')
const ledgerStatus = ref('')
const reportStatus = ref('')
const confirmGoal = ref<{ id: string; title: string } | null>(null)
const attachmentsOpen = ref<Record<string, boolean>>({})
const selectedCalendarDate = ref<string | null>(null)
const calendarImportOpen = ref(false)
const calendarImportText = ref('')
const calendarImportStatus = ref('')
const calendarGrid = ref<HTMLElement | null>(null)
const focusedCalendarDate = ref<string | null>(readStorage('planna.calendarFocus'))
const attachmentMessage = ref<Record<string, string>>({})
const attachmentPreviewUrls = ref<Record<string, string>>({})
const nowTick = ref(Date.now())
const theme = ref<Theme>(normalizeTheme(readStorage('planna.theme')))
const compact = ref(readStorage('planna.compact') === '1')
const sentReminders = new Set<string>()
const notifierPromise = createNotifier()
let reminderTimer: ReturnType<typeof setInterval> | null = null
let aiTickTimer: ReturnType<typeof setInterval> | null = null
let focusTimer: ReturnType<typeof setInterval> | null = null

const email = ref('')
const code = ref('')
const title = ref('')
const habitTitle = ref('')
const habitTarget = ref('1')
const habitUnit = ref('次')
const syncing = ref(false)

const signedIn = computed(() => session.status.value === 'signed_in')
const signedOut = computed(() => session.status.value === 'signed_out')
const errorText = computed(() => {
  const error =
    session.error.value ??
    tasks.error.value ??
    habits.error.value ??
    goals.error.value ??
    notes.error.value ??
    focus.error.value
  if (!error) return null
  const messages: Record<string, string> = {
    INVALID_EMAIL: '请输入有效邮箱',
    INVALID_CREDENTIALS: '邮箱或验证码格式不正确',
    CODE_REQUEST_FAILED: '验证码发送失败，请重试',
    SIGN_IN_FAILED: '登录失败，请检查验证码',
    SYNC_FAILED: '同步失败，稍后自动重试',
    NOT_SIGNED_IN: '请先登录',
    INVALID_TITLE: '标题不能为空且不超过 500 字',
    SAVE_FAILED: '保存失败，请重试',
    LOAD_FAILED: '加载失败，请重试',
    HABITS_LOAD_FAILED: '习惯加载失败，请重试',
    HABIT_CREATE_FAILED: '习惯创建失败，请重试',
    INVALID_HABIT: '习惯名称需为 1~200 字，目标需在 1~1000 之间',
    CHECK_IN_FAILED: '打卡失败，请重试',
    HABIT_PAUSE_FAILED: '暂停操作失败，请重试',
    HABIT_UPDATE_FAILED: '习惯更新失败，请重试',
    INVALID_GOAL: '目标名称需为 1~200 字',
    GOAL_SIGNED_OUT: '请先登录再管理目标',
    GOAL_NOT_FOUND: '目标不存在',
    GOAL_FAILED: '目标操作失败，请重试',
    AI_UNAVAILABLE: 'AI 服务当前不可用',
    RATE_LIMITED: 'AI 请求过于频繁，请稍后再试',
    INVALID_GUIDANCE: 'AI 拆解提示不能超过 2000 字',
    INVALID_NOTE: '笔记内容不能为空，且标题/正文不能超长',
    NOTE_SIGNED_OUT: '请先登录再使用笔记',
    NOTE_NOT_FOUND: '笔记不存在',
    NOTE_FAILED: '笔记操作失败，请重试',
    FOCUS_SESSION_ACTIVE: '已有进行中的专注，先完成或放弃当前会话',
    FOCUS_SESSION_FINISHED: '该专注会话已结束',
    FOCUS_SIGNED_OUT: '请先登录再开始专注',
    FOCUS_IDLE: '当前没有进行中的专注',
    FOCUS_FAILED: '专注操作失败，请重试',
    VERSION_CONFLICT: '专注状态已在其他设备更新，请刷新后重试',
    HABIT_NOT_FOUND: '习惯不存在'
  }
  return messages[error] ?? '操作失败，请重试'
})
const syncSummary = computed(() => {
  const summary = session.lastSync.value
  if (!summary) return null
  return `已推送 ${summary.pushed}、拉取 ${summary.pulled}、冲突 ${summary.conflicts.length}`
})

watch(baseUrl, (value) => {
  try {
    localStorage.setItem('planna.baseUrl', value)
  } catch {
    // Storage may be unavailable in hardened environments; the session still works in memory.
  }
})

async function requestCode(): Promise<void> {
  const ok = await session.requestCode(email.value)
  if (ok) code.value = ''
}

async function submitLogin(): Promise<void> {
  if (await session.signIn(email.value, code.value)) {
    await tasks.refresh()
    await habits.refresh()
    await goals.refresh()
    await notes.refresh()
    await calendar.load()
    await focus.recover()
    await focus.loadRecent(30)
    await refreshFocusStats()
  }
}

async function submitHabit(): Promise<void> {
  const target = Number.parseInt(habitTarget.value, 10)
  const created = await habits.create(habitTitle.value, Number.isFinite(target) ? target : 1, habitUnit.value)
  if (created) {
    habitTitle.value = ''
    habitTarget.value = '1'
  }
}

async function submitGoal(): Promise<void> {
  const created = await goals.create(goalTitle.value, goalTargetDate.value || null)
  if (created) {
    goalTitle.value = ''
    goalTargetDate.value = ''
  }
}

function startEditGoal(goalId: string): void {
  const goal = goals.goals.value.find((candidate) => candidate.id === goalId)
  if (!goal) return
  editingGoal.value = goalId
  editGoalTitle.value = goal.title
  editGoalDescription.value = goal.description
  editGoalTargetDate.value = goal.targetDate ?? ''
}

function cancelEditGoal(): void {
  editingGoal.value = null
}

async function saveEditGoal(goalId: string): Promise<void> {
  const saved = await goals.updateDetails(goalId, {
    title: editGoalTitle.value,
    description: editGoalDescription.value,
    targetDate: editGoalTargetDate.value === '' ? null : editGoalTargetDate.value
  })
  if (saved) editingGoal.value = null
}

function requestDeleteGoal(goalId: string): void {
  const goal = goals.goals.value.find((candidate) => candidate.id === goalId)
  if (goal) confirmGoal.value = { id: goal.id, title: goal.title }
}

async function confirmDeleteGoal(): Promise<void> {
  const target = confirmGoal.value
  confirmGoal.value = null
  if (!target) return
  const goal = goals.goals.value.find((candidate) => candidate.id === target.id)
  if (goal) await goals.deleteGoal(goal)
}

function aiRetryRemaining(): number {
  if (goals.aiRetryAt.value === null) return 0
  return Math.max(0, Math.ceil((goals.aiRetryAt.value - nowTick.value) / 1000))
}

async function previewGoalBreakdown(goalId: string): Promise<void> {
  await goals.previewBreakdown(goalId, goalGuidance.value[goalId] ?? '')
}

async function retryGoalBreakdown(goalId: string): Promise<void> {
  await goals.retryPreview(goalId, goalGuidance.value[goalId] ?? '')
}

async function addGoalSuggestion(goalId: string, suggestion: GoalBreakdownSuggestion): Promise<void> {
  await goals.addSuggestion(goalId, suggestion)
}

async function addAllGoalSuggestions(goalId: string): Promise<void> {
  await goals.addAllSuggestions(goalId)
}

async function submitMilestone(goalId: string): Promise<void> {
  const draft = milestoneDrafts.value[goalId] ?? ''
  if (await goals.addMilestone(goalId, draft)) {
    milestoneDrafts.value = { ...milestoneDrafts.value, [goalId]: '' }
  }
}

function goalSuggestions(goalId: string): GoalBreakdownSuggestion[] {
  return goals.suggestions.value[goalId] ?? []
}

async function toggleGoalMilestone(goalId: string, milestoneId: string): Promise<void> {
  const goal = goals.goals.value.find((candidate) => candidate.id === goalId)
  const milestone = goal?.milestones.find((candidate) => candidate.id === milestoneId)
  if (milestone) await goals.toggleMilestone(goalId, milestone)
}

async function submitNote(): Promise<void> {
  const created = await notes.create(
    noteTitle.value,
    noteContent.value,
    noteTaskId.value === '' ? null : noteTaskId.value
  )
  if (created) {
    noteTitle.value = ''
    noteContent.value = ''
    noteTaskId.value = ''
    await notes.refresh()
  }
}

function taskTitle(taskId: string | null): string | null {
  if (!taskId) return null
  return tasks.tasks.value.find((task) => task.id === taskId)?.title ?? null
}

function startEditNote(noteId: string, title: string, content: string): void {
  editingNote.value = noteId
  editNoteTitle.value = title
  editNoteContent.value = content
}

function cancelEditNote(): void {
  editingNote.value = null
  editNoteTitle.value = ''
  editNoteContent.value = ''
}

async function saveEditNote(noteId: string, version: number): Promise<void> {
  if (await notes.update(noteId, version, { title: editNoteTitle.value, content: editNoteContent.value })) {
    editingNote.value = null
    editNoteTitle.value = ''
    editNoteContent.value = ''
  }
}

async function toggleCalendarDay(date: string, kind: string): Promise<void> {
  selectedCalendarDate.value = date
  await calendar.toggle(date, kind)
}

async function clearCalendarOverride(): Promise<void> {
  if (!selectedCalendarDate.value) return
  await calendar.clear(selectedCalendarDate.value)
  selectedCalendarDate.value = null
}

function calendarButtons(): HTMLButtonElement[] {
  const grid = calendarGrid.value
  return grid ? Array.from(grid.querySelectorAll<HTMLButtonElement>('button[data-date]')) : []
}

function focusCalendarBoundary(which: 'first' | 'last'): void {
  const buttons = calendarButtons()
  const target = which === 'first' ? buttons[0] : buttons[buttons.length - 1]
  if (target?.dataset.date) focusedCalendarDate.value = target.dataset.date
  target?.focus()
}

async function focusCalendarDay(date: string, delta: number): Promise<void> {
  const buttons = calendarButtons()
  const index = buttons.findIndex((button) => button.dataset.date === date)
  if (index < 0) return
  const action = arrowAction(index, delta, buttons.length)
  if (action === 'next') {
    await calendar.nextMonth()
    await nextTick()
    focusCalendarBoundary('first')
    return
  }
  if (action === 'previous') {
    await calendar.previousMonth()
    await nextTick()
    focusCalendarBoundary('last')
    return
  }
  const target = buttons[moveFocusIndex(index, delta, buttons.length)]
  if (target?.dataset.date) focusedCalendarDate.value = target.dataset.date
  target?.focus()
}

const calendarYears = computed(() => {
  const base = calendar.year.value
  return Array.from({ length: 11 }, (_, index) => base - 5 + index)
})

async function jumpCalendar(): Promise<void> {
  await calendar.load()
  await nextTick()
  focusedCalendarDate.value = calendar.days.value[0]?.date ?? null
}

async function changeCalendarMonth(delta: number): Promise<void> {
  if (delta < 0) await calendar.previousMonth()
  else await calendar.nextMonth()
  await nextTick()
  focusCalendarBoundary(delta < 0 ? 'last' : 'first')
}

async function importCalendarOverrides(): Promise<void> {
  const result = await calendar.importLines(calendarImportText.value)
  if (!result) {
    calendarImportStatus.value = '导入失败：' + (calendar.error.value ?? 'UNKNOWN')
    return
  }
  calendarImportStatus.value = `导入 ${result.imported} 条，忽略 ${result.errors} 条`
  if (result.imported > 0) calendarImportText.value = ''
}

async function previewNoteAttachment(
  noteId: string,
  attachment: { id: string; contentType: string }
): Promise<void> {
  const token = session.token.value
  if (!token) return
  try {
    const bytes = await bridge.noteAttachmentBytes(
      baseUrl.value,
      token,
      noteId,
      attachment.id
    )
    const previous = attachmentPreviewUrls.value[attachment.id]
    if (previous) URL.revokeObjectURL(previous)
    const url = URL.createObjectURL(
      new Blob([new Uint8Array(bytes)], { type: attachment.contentType })
    )
    attachmentPreviewUrls.value = { ...attachmentPreviewUrls.value, [attachment.id]: url }
  } catch (failure) {
    attachmentMessage.value = {
      ...attachmentMessage.value,
      [attachment.id]: '预览失败：' + attachmentErrorMessage(
        failure instanceof Error ? failure.message.split(':').slice(1).join(':') : null
      )
    }
  }
}

async function toggleNoteAttachments(noteId: string): Promise<void> {
  const next = !(attachmentsOpen.value[noteId] ?? false)
  attachmentsOpen.value = { ...attachmentsOpen.value, [noteId]: next }
  if (next) await notes.loadAttachments(noteId)
}

async function uploadNoteAttachment(noteId: string): Promise<void> {
  const path = await notes.pickAttachment()
  if (!path) return
  const saved = await notes.uploadAttachment(noteId, path)
  attachmentMessage.value = {
    ...attachmentMessage.value,
    [noteId]: saved ? '已上传' : '上传失败：' + attachmentErrorMessage(notes.attachmentError.value)
  }
}

async function downloadNoteAttachment(noteId: string, attachmentId: string): Promise<void> {
  const path = await notes.downloadAttachment(noteId, attachmentId)
  attachmentMessage.value = {
    ...attachmentMessage.value,
    [noteId]: path ? '已保存：' + path : notes.attachmentError.value ? '下载失败：' + notes.attachmentError.value : '已取消'
  }
}

async function deleteNoteAttachment(noteId: string, attachmentId: string): Promise<void> {
  const removed = await notes.deleteAttachment(noteId, attachmentId)
  attachmentMessage.value = {
    ...attachmentMessage.value,
    [noteId]: removed ? '已删除附件' : '删除失败：' + attachmentErrorMessage(notes.attachmentError.value)
  }
}

async function removeNote(noteId: string, version: number): Promise<void> {
  const note = notes.notes.value.find((candidate) => candidate.id === noteId)
  if (note) await notes.remove({ ...note, version })
}

async function startFocus(plannedSeconds: number): Promise<void> {
  await focus.start(plannedSeconds, focusTaskId.value === '' ? null : focusTaskId.value)
}

async function pauseFocus(): Promise<void> {
  await focus.pause()
}

async function resumeFocus(): Promise<void> {
  await focus.resume()
}

async function completeFocus(): Promise<void> {
  const minutes = Number.parseInt(focusMinutes.value, 10)
  const override = Number.isFinite(minutes) && minutes >= 0 ? minutes * 60 : undefined
  if (await focus.complete(override)) {
    focusMinutes.value = ''
    await refreshFocusStats()
  }
}

async function refreshFocusStats(): Promise<void> {
  const today = localDate()
  await focus.loadStats(
    addDays(today, -(focusStatsDays.value - 1)),
    today,
    Intl.DateTimeFormat().resolvedOptions().timeZone || 'Asia/Shanghai'
  )
}

async function setFocusStatsDays(days: number): Promise<void> {
  focusStatsDays.value = days
  await refreshFocusStats()
}

function toggleModule(key: ModuleKey): void {
  modules.toggle(key)
}

async function startTaskFocus(taskId: string): Promise<void> {
  focusTaskId.value = taskId
  await startFocus(1500)
}

function toggleTaskDetail(taskId: string): void {
  expandedTask.value = expandedTask.value === taskId ? null : taskId
  if (expandedTask.value) void focus.loadTaskHistory(taskId)
}

function startEditTaskNote(noteId: string, title: string, content: string): void {
  editingTaskNote.value = noteId
  editTaskNoteTitle.value = title
  editTaskNoteContent.value = content
}

function cancelEditTaskNote(): void {
  editingTaskNote.value = null
  editTaskNoteTitle.value = ''
  editTaskNoteContent.value = ''
}

async function saveEditTaskNote(noteId: string, version: number): Promise<void> {
  const saved = await notes.update(noteId, version, {
    title: editTaskNoteTitle.value,
    content: editTaskNoteContent.value
  })
  if (saved) cancelEditTaskNote()
}

async function addTaskNote(taskId: string): Promise<void> {
  const content = (taskNoteDrafts.value[taskId] ?? '').trim()
  if (content === '') return
  const created = await notes.create('', content, taskId)
  if (created) taskNoteDrafts.value = { ...taskNoteDrafts.value, [taskId]: '' }
}

function taskSessions(taskId: string) {
  return focus.taskHistory.value[taskId] ?? []
}

function barHeight(seconds: number): number {
  const days = focus.stats.value?.days ?? []
  const max = Math.max(1, ...days.map((day) => day.actualSeconds))
  return Math.max(4, Math.round((seconds / max) * 48))
}

async function checkInHabit(habitId: string): Promise<void> {
  await habits.checkIn(habitId)
}

async function toggleHabitPause(habitId: string): Promise<void> {
  await habits.togglePause(habitId)
}

async function submitTask(): Promise<void> {
  const created = await tasks.add(title.value)
  if (created) title.value = ''
}

async function syncNow(): Promise<void> {
  if (syncing.value) return
  syncing.value = true
  try {
    if (await session.sync()) {
      await tasks.refresh()
      await habits.refresh()
      await goals.refresh()
      await notes.refresh()
      await calendar.load()
      await focus.loadRecent(30)
      await refreshOutbox()
    }
  } finally {
    syncing.value = false
  }
}

async function signOut(): Promise<void> {
  await session.signOut()
  tasks.tasks.value = []
  habits.habits.value = []
  habits.todayLogs.value = {}
  goals.goals.value = []
  notes.notes.value = []
  notes.query.value = ''
  focus.session.value = null
  focus.snapshot.value = null
}

function reminderFor(habitId: string): HabitReminder {
  return reminders.value.find((item) => item.habitId === habitId) ?? {
    habitId,
    time: '09:00',
    enabled: false
  }
}

function reminderTime(habitId: string): string {
  return reminderFor(habitId).time
}

function upsertReminder(habitId: string, patch: Partial<HabitReminder>): void {
  const current = reminderFor(habitId)
  const next: HabitReminder = { ...current, ...patch, habitId }
  reminders.value = [
    ...reminders.value.filter((item) => item.habitId !== habitId),
    next
  ]
}

function setReminderTime(habitId: string, event: Event): void {
  const value = (event.target as HTMLInputElement).value
  if (!isReminderTime(value)) return
  upsertReminder(habitId, { time: value })
}

function setReminderEnabled(habitId: string, event: Event): void {
  upsertReminder(habitId, { enabled: (event.target as HTMLInputElement).checked })
}

function startEditHabit(habitId: string): void {
  const habit = habits.habits.value.find((candidate) => candidate.id === habitId)
  if (!habit) return
  editingHabit.value = habitId
  editHabitTitle.value = habit.title
  editHabitTarget.value = String(habit.targetQuantity)
  editHabitUnit.value = habit.unit
  editHabitSchedule.value = habit.scheduleType
  editHabitWeekdays.value = habit.weekdays ? [...habit.weekdays] : []
}

function toggleEditWeekday(day: number): void {
  editHabitWeekdays.value = editHabitWeekdays.value.includes(day)
    ? editHabitWeekdays.value.filter((value) => value !== day)
    : [...editHabitWeekdays.value, day].sort((left, right) => left - right)
}

function scheduleLabel(habit: { scheduleType: string; weekdays: number[] | null }): string {
  if (habit.scheduleType === 'daily') return '每日'
  if (habit.scheduleType === 'weekdays') return '工作日'
  const names = ['一', '二', '三', '四', '五', '六', '日']
  return `每周${(habit.weekdays ?? []).map((day) => names[day - 1] ?? day).join('/')}`
}

function cancelEditHabit(): void {
  editingHabit.value = null
}

async function saveEditHabit(): Promise<void> {
  const habitId = editingHabit.value
  if (!habitId) return
  const target = Number.parseInt(editHabitTarget.value, 10)
  const saved = await habits.updateDetails(habitId, {
    title: editHabitTitle.value,
    scheduleType: editHabitSchedule.value,
    weekdays: editHabitSchedule.value === 'weekly' ? editHabitWeekdays.value : [],
    targetQuantity: Number.isFinite(target) ? target : 1,
    unit: editHabitUnit.value
  })
  if (saved) editingHabit.value = null
}

async function refreshOutbox(): Promise<void> {
  try {
    outbox.value = await bridge.outboxStatus()
  } catch {
    outbox.value = { pending: 0, failed: 0 }
  }
  try {
    issues.value = await bridge.outboxIssues()
  } catch {
    issues.value = []
  }
  try {
    conflicts.value = await bridge.listConflicts()
  } catch {
    conflicts.value = []
  }
}

async function exportFocusReport(): Promise<void> {
  reportStatus.value = ''
  const stats = focus.stats.value
  if (!stats) return
  const path = await focus.saveReport(focusReportFilename(stats), focusStatsCsv(stats))
  reportStatus.value = path ? '已保存：' + path : '保存失败（' + (focus.error.value ?? 'UNKNOWN') + '）'
}

async function exportFocusJson(): Promise<void> {
  reportStatus.value = ''
  const stats = focus.stats.value
  if (!stats) return
  const path = await focus.saveReport(
    focusReportFilename(stats, 'json'),
    focusStatsJson(stats)
  )
  reportStatus.value = path ? '已保存：' + path : '保存失败（' + (focus.error.value ?? 'UNKNOWN') + '）'
}

async function saveFocusReportAs(): Promise<void> {
  reportStatus.value = ''
  const stats = focus.stats.value
  if (!stats) return
  const path = await focus.saveReportAs(focusReportFilename(stats), focusStatsCsv(stats))
  reportStatus.value = path ? '已保存：' + path : focus.error.value ? '保存失败：' + focus.error.value : '已取消'
}

async function copyFocusReport(): Promise<void> {
  reportStatus.value = ''
  const stats = focus.stats.value
  if (!stats) return
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      await navigator.clipboard.writeText(focusStatsCsv(stats))
      reportStatus.value = '已复制 CSV'
    } else {
      reportStatus.value = '剪贴板不可用'
    }
  } catch {
    reportStatus.value = '复制失败，请重试'
  }
}

async function exportLedger(): Promise<void> {
  ledgerStatus.value = ''
  try {
    ledgerText.value = await bridge.exportConflicts()
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      await navigator.clipboard.writeText(ledgerText.value)
      ledgerStatus.value = '已复制到剪贴板'
    } else {
      ledgerStatus.value = '请手动复制下方内容'
    }
  } catch {
    ledgerStatus.value = '导出失败，请重试'
  }
}

async function resolveConflictAction(mutationId: string, strategy: ConflictStrategy): Promise<void> {
  await bridge.resolveConflict(mutationId, strategy)
  await refreshOutbox()
  await tasks.refresh()
  await habits.refresh()
  await notes.refresh()
}

async function discardIssue(mutationId: string): Promise<void> {
  await bridge.discardMutation(mutationId)
  await refreshOutbox()
  await habits.refresh()
  await notes.refresh()
}

function quietHours(): QuietHours | null {
  if (!isReminderTime(quietStart.value) || !isReminderTime(quietEnd.value)) return null
  if (quietStart.value === quietEnd.value) return null
  return { start: quietStart.value, end: quietEnd.value }
}

async function tickReminders(): Promise<void> {
  if (!signedIn.value || habits.habits.value.length === 0) return
  const notifier = await notifierPromise
  await runReminders(
    notifier,
    habits.habits.value,
    reminders.value,
    new Date(),
    sentReminders,
    quietHours(),
    (habitId, date) => {
      const log = habits.todayLogs.value[habitId]
      return log?.logDate === date && log.status === 'done'
    }
  )
}

watch(
  () => habits.habits.value,
  (list) => {
    for (const habit of list) {
      if (!reminders.value.some((item) => item.habitId === habit.id)) {
        reminders.value = [...reminders.value, { habitId: habit.id, time: '09:00', enabled: false }]
      }
    }
  },
  { deep: false }
)

watch(
  reminders,
  (value) => writeStorage('planna.habitReminders', JSON.stringify(value)),
  { deep: true }
)

watch(focusedCalendarDate, (value) => {
  writeStorage('planna.calendarFocus', value ?? '')
})

watch(theme, (value) => {
  writeStorage('planna.theme', value)
  if (value === 'auto') delete document.documentElement.dataset.theme
  else document.documentElement.dataset.theme = value
}, { immediate: true })

watch(compact, (value) => {
  writeStorage('planna.compact', value ? '1' : '0')
})

watch(focusRestMinutes, (value) => {
  writeStorage('planna.focusRestMinutes', value)
})

watch([quietStart, quietEnd], ([start, end]) => {
  if (start === '' && end === '') {
    writeStorage('planna.quietHours', '')
    return
  }
  writeStorage('planna.quietHours', JSON.stringify({ start, end }))
})

onUnmounted(() => {
  if (reminderTimer) clearInterval(reminderTimer)
  if (focusTimer) clearInterval(focusTimer)
  if (aiTickTimer) clearInterval(aiTickTimer)
})

onMounted(async () => {
  if (await session.restore()) {
    await tasks.refresh()
    await habits.refresh()
    await goals.refresh()
    await notes.refresh()
    await calendar.load()
    await focus.recover()
    await focus.loadRecent(30)
    await refreshFocusStats()
    await refreshOutbox()
  }
  reminderTimer = setInterval(() => {
    void tickReminders()
  }, 30_000)
  aiTickTimer = setInterval(() => {
    nowTick.value = Date.now()
  }, 1000)
  focusTimer = setInterval(() => {
    focus.tick()
    focus.tickRest()
  }, 1_000)
})
</script>

<template>
  <a class="skip-link" href="#tasks">跳到主内容</a>
  <main id="main-content" class="app" :class="densityClass(compact)">
    <nav class="app__nav" aria-label="功能导航">
      <a v-for="link in navLinks(modules.visibility.value)" :key="link.id" :href="'#' + link.id">
        {{ link.label }}
      </a>
    </nav>
    <header class="app__header">
      <h1>Planna 普拉清单</h1>
      <p class="app__subtitle">本地优先 · 离线可用 · 登录后同步</p>
      <div class="app__prefs" aria-label="外观设置">
        <label>
          主题
          <select v-model="theme" class="input input--narrow" aria-label="主题">
            <option value="auto">跟随系统</option>
            <option value="light">浅色</option>
            <option value="dark">深色</option>
          </select>
        </label>
        <label class="app__compact">
          <input v-model="compact" type="checkbox" />
          紧凑密度
        </label>
      </div>
    </header>
    <ConfirmDialog
      :open="confirmGoal !== null"
      title="删除目标"
      :message="`删除“${confirmGoal?.title ?? ''}”后无法恢复，确定继续？`"
      confirm-label="删除"
      @confirm="confirmDeleteGoal"
      @cancel="confirmGoal = null"
    />

    <section v-if="signedOut" class="card" aria-labelledby="login-title">
      <h2 id="login-title" class="card__title">登录</h2>
      <p class="card__hint">使用邮箱一次性验证码登录；离线时仍可查看本地任务。</p>
      <label class="field">
        <span>服务器地址</span>
        <input v-model="baseUrl" class="input" type="url" autocomplete="off" spellcheck="false" />
      </label>
      <label class="field">
        <span>邮箱</span>
        <input v-model="email" class="input" type="email" autocomplete="email" />
      </label>
      <div class="field">
        <span>验证码</span>
        <div class="field__row">
          <input v-model="code" class="input" type="text" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
          <button class="button button--ghost" type="button" :disabled="session.busy.value" @click="requestCode">
            获取验证码
          </button>
        </div>
      </div>
      <button class="button" type="button" :disabled="session.busy.value" @click="submitLogin">登录</button>
    </section>

    <template v-if="signedIn">
      <section class="card" aria-labelledby="session-title">
        <div class="card__row">
          <h2 id="session-title" class="card__title">已登录</h2>
          <button class="button button--ghost" type="button" @click="signOut">退出</button>
        </div>
        <div class="card__row">
          <button class="button" type="button" :disabled="syncing" @click="syncNow">立即同步</button>
          <span v-if="syncing" class="status">同步中…</span>
          <span v-else-if="syncSummary" class="status">{{ syncSummary }}</span>
        </div>
        <div class="card__row modules" aria-label="显示模块">
          <span class="habits__meta">显示模块</span>
          <label v-for="(label, key) in MODULE_LABELS" :key="key" class="modules__item">
            <input
              type="checkbox"
              :checked="modules.visibility.value[key]"
              @change="toggleModule(key)"
            />
            {{ label }}
          </label>
        </div>
      </section>

      <form id="tasks" class="composer" @submit.prevent="submitTask">
        <label class="composer__label" for="task-title">新增任务</label>
        <div class="composer__row">
          <input
            id="task-title"
            v-model="title"
            class="input"
            type="text"
            maxlength="500"
            placeholder="例如：写周报"
            autocomplete="off"
          />
          <button class="button" type="submit" :disabled="session.busy.value || title.trim() === ''">添加</button>
        </div>
      </form>

      <ul v-if="tasks.tasks.value.length > 0" class="tasks" aria-label="任务列表">
        <li v-for="task in tasks.tasks.value" :key="task.id" class="tasks__item">
          <div class="tasks__row">
            <span class="tasks__title">{{ task.title }}</span>
            <span class="tasks__meta">v{{ task.version }}</span>
            <button class="button button--ghost" type="button" @click="toggleTaskDetail(task.id)">
              详情
            </button>
            <button class="button button--ghost" type="button" @click="startTaskFocus(task.id)">
              专注
            </button>
          </div>
          <div v-if="expandedTask === task.id" class="task-detail">
            <p class="tasks__meta">状态：{{ task.status }} · 版本 v{{ task.version }}</p>
            <ul v-if="taskSessions(task.id).length > 0" class="focus__days">
              <li v-for="session in taskSessions(task.id)" :key="session.id" class="focus__day">
                <span>{{ session.startedAt.slice(0, 10) }}</span>
                <span>{{ Math.round(session.actualSeconds / 60) }} 分钟 · {{ session.state }}</span>
              </li>
            </ul>
            <p v-else class="empty">该任务还没有专注记录。</p>
            <button
              v-if="focus.taskCursors.value[task.id]"
              class="button button--ghost"
              type="button"
              @click="focus.loadMoreTaskHistory(task.id)"
            >
              加载更多
            </button>
            <div class="composer__row">
              <input
                v-model="taskNoteDrafts[task.id]"
                class="input"
                type="text"
                maxlength="20000"
                placeholder="为任务添加笔记"
                :aria-label="`为 ${task.title} 添加笔记`"
                @keyup.enter="addTaskNote(task.id)"
              />
              <button class="button button--ghost" type="button" @click="addTaskNote(task.id)">
                添加笔记
              </button>
            </div>
            <template v-if="notesForTask(notes.notes.value, task.id).length > 0">
              <p class="habits__meta">关联笔记</p>
              <ul class="goals__milestones">
                <li
                  v-for="note in notesForTask(notes.notes.value, task.id)"
                  :key="note.id"
                  class="goals__milestone"
                >
                  <template v-if="editingTaskNote === note.id">
                    <div class="composer__row">
                      <input
                        v-model="editTaskNoteTitle"
                        class="input"
                        type="text"
                        maxlength="200"
                        placeholder="标题（可选）"
                        aria-label="笔记标题"
                      />
                      <button class="button" type="button" @click="saveEditTaskNote(note.id, note.version)">
                        保存
                      </button>
                      <button class="button button--ghost" type="button" @click="cancelEditTaskNote">
                        取消
                      </button>
                    </div>
                    <textarea
                      v-model="editTaskNoteContent"
                      class="input"
                      rows="3"
                      maxlength="20000"
                      aria-label="笔记正文"
                    ></textarea>
                  </template>
                  <template v-else>
                    <span>{{ note.title || '无标题' }}</span>
                    <span class="habits__meta">{{ note.content.slice(0, 80) }}</span>
                    <button
                      class="button button--ghost"
                      type="button"
                      @click="startEditTaskNote(note.id, note.title, note.content)"
                    >
                      编辑
                    </button>
                  </template>
                </li>
              </ul>
            </template>
          </div>
        </li>
      </ul>
      <p v-else-if="!tasks.loading.value" class="empty">还没有任务，先添加一条。</p>
    </template>

    <section v-if="signedIn && modules.visibility.value.notes" id="notes" class="card" aria-labelledby="notes-title">
      <h2 id="notes-title" class="card__title">笔记</h2>
      <div class="composer__row">
        <input
          v-model="notes.query.value"
          class="input"
          type="search"
          placeholder="搜索标题或正文"
          aria-label="搜索笔记"
          @input="notes.refresh()"
        />
      </div>
      <form class="composer" @submit.prevent="submitNote">
        <div class="composer__row">
          <input
            v-model="noteTitle"
            class="input"
            type="text"
            maxlength="200"
            placeholder="标题（可选）"
            aria-label="笔记标题"
          />
        </div>
        <div class="composer__row">
          <select v-model="noteTaskId" class="input" aria-label="关联任务">
            <option value="">不关联任务</option>
            <option v-for="task in tasks.tasks.value" :key="task.id" :value="task.id">{{ task.title }}</option>
          </select>
        </div>
        <textarea
          v-model="noteContent"
          class="input input--multiline"
          maxlength="20000"
          rows="3"
          placeholder="记点什么…"
          aria-label="笔记正文"
        ></textarea>
        <div class="composer__row">
          <button class="button" type="submit" :disabled="notes.loading.value || (noteTitle.trim() === '' && noteContent.trim() === '')">
            保存笔记
          </button>
        </div>
      </form>
      <ul v-if="notes.notes.value.length > 0" class="notes">
        <li v-for="note in notes.notes.value" :key="note.id" class="notes__item">
          <template v-if="editingNote === note.id">
            <input
              v-model="editNoteTitle"
              class="input"
              type="text"
              maxlength="200"
              placeholder="标题（可选）"
              aria-label="编辑笔记标题"
            />
            <textarea
              v-model="editNoteContent"
              class="input input--multiline"
              maxlength="20000"
              rows="3"
              aria-label="编辑笔记正文"
            ></textarea>
            <div class="composer__row">
              <button class="button" type="button" @click="saveEditNote(note.id, note.version)">保存</button>
              <button class="button button--ghost" type="button" @click="cancelEditNote">取消</button>
            </div>
          </template>
          <template v-else>
            <div class="notes__header">
              <span class="notes__title">{{ note.title || '未命名笔记' }}</span>
              <span class="notes__meta">
                <template v-if="taskTitle(note.taskId)">关联：{{ taskTitle(note.taskId) }} · </template>
                v{{ note.version }}
              </span>
            </div>
            <p class="notes__content">{{ note.content }}</p>
            <div class="composer__row">
              <button class="button button--ghost" type="button" @click="startEditNote(note.id, note.title, note.content)">
                编辑
              </button>
              <button class="button button--ghost" type="button" @click="removeNote(note.id, note.version)">
                删除
              </button>
            </div>
            <div class="composer__row">
              <button class="button button--ghost" type="button" @click="toggleNoteAttachments(note.id)">
                附件（{{ (notes.attachments.value[note.id] ?? []).length }}）
              </button>
              <button
                v-if="attachmentsOpen[note.id]"
                class="button button--ghost"
                type="button"
                @click="uploadNoteAttachment(note.id)"
              >
                上传附件
              </button>
            </div>
            <div v-if="attachmentsOpen[note.id]" class="attachments">
              <p v-if="attachmentMessage[note.id]" class="status">{{ attachmentMessage[note.id] }}</p>
              <ul v-if="(notes.attachments.value[note.id] ?? []).length > 0" class="goals__milestones">
                <li
                  v-for="attachment in notes.attachments.value[note.id]"
                  :key="attachment.id"
                  class="goals__milestone"
                >
                  <span>{{ attachment.filename }}</span>
                  <span class="habits__meta">{{ Math.ceil(attachment.sizeBytes / 1024) }} KB</span>
                  <button class="button button--ghost" type="button" @click="downloadNoteAttachment(note.id, attachment.id)">
                    下载
                  </button>
                  <button
                    v-if="canPreviewAttachment(attachment.contentType)"
                    class="button button--ghost"
                    type="button"
                    @click="previewNoteAttachment(note.id, attachment)"
                  >
                    预览
                  </button>
                  <img
                    v-if="attachmentPreviewUrls[attachment.id]"
                    class="attachment-preview"
                    :src="attachmentPreviewUrls[attachment.id]"
                    :alt="attachment.filename"
                  />
                  <button class="button button--ghost" type="button" @click="deleteNoteAttachment(note.id, attachment.id)">
                    删除
                  </button>
                </li>
              </ul>
              <p v-else class="empty">暂无附件（笔记需先同步到服务端）。</p>
            </div>
          </template>
        </li>
      </ul>
      <p v-else class="empty">还没有笔记，记录一条想法或会议要点。</p>
    </section>

    <section v-if="signedIn && modules.visibility.value.focus" id="focus" class="card" aria-labelledby="focus-title">
      <h2 id="focus-title" class="card__title">专注</h2>
      <div class="composer__row">
        <label class="habits__meta">
          休息时长
          <select v-model="focusRestMinutes" class="input input--narrow" aria-label="休息时长（分钟）">
            <option value="0">不休息</option>
            <option value="5">5 分钟</option>
            <option value="10">10 分钟</option>
            <option value="15">15 分钟</option>
          </select>
        </label>
      </div>
      <template v-if="focus.snapshot.value">
        <p class="focus__time">{{ formatDuration(focus.seconds.value) }}</p>
        <p class="status">
          计划 {{ formatDuration(focus.snapshot.value.plannedSeconds) }} ·
          {{ focus.snapshot.value.state === 'active' ? '进行中' : '已暂停' }}
        </p>
        <p v-if="focus.driftSeconds.value > 60" class="status status--error">
          检测到约 {{ Math.round(focus.driftSeconds.value / 60) }} 分钟的后台中断，可在完成前修正实际专注时长。
        </p>
        <div class="composer__row">
          <button v-if="focus.snapshot.value.state === 'active'" class="button" type="button" @click="pauseFocus">
            暂停
          </button>
          <button v-else class="button" type="button" @click="resumeFocus">继续</button>
          <input
            v-model="focusMinutes"
            class="input input--narrow"
            type="number"
            min="0"
            max="1440"
            placeholder="实际分钟"
            aria-label="实际专注分钟"
          />
          <button class="button button--ghost" type="button" @click="completeFocus">完成</button>
        </div>
      </template>
      <p v-if="focus.restEndsAt.value" class="status">
        休息中 {{ formatDuration(focus.restRemaining.value) }}
        <button class="button button--ghost" type="button" @click="focus.skipRest()">跳过休息</button>
      </p>
      <p v-else-if="focus.restHint.value" class="status">{{ focus.restHint.value }}</p>
      <template v-else>
        <p class="empty">开始一段专注，退出后重新打开会按服务端记录恢复。</p>
        <div class="composer__row">
          <select v-model="focusTaskId" class="input" aria-label="关联任务">
            <option value="">不关联任务</option>
            <option v-for="task in tasks.tasks.value" :key="task.id" :value="task.id">{{ task.title }}</option>
          </select>
          <button class="button" type="button" :disabled="focus.loading.value" @click="startFocus(1500)">
            开始 25 分钟
          </button>
          <button class="button button--ghost" type="button" :disabled="focus.loading.value" @click="startFocus(3000)">
            开始 50 分钟
          </button>
        </div>
      </template>
      <div class="focus__stats">
        <div class="issues__header">
          <h3 class="focus__stats-title">近 {{ focusStatsDays }} 天专注</h3>
          <button class="button button--ghost" type="button" @click="setFocusStatsDays(7)">7 天</button>
          <button class="button button--ghost" type="button" @click="setFocusStatsDays(30)">30 天</button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="!focus.stats.value"
            @click="copyFocusReport"
          >
            复制 CSV
          </button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="!focus.stats.value"
            @click="exportFocusReport"
          >
            导出 CSV
          </button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="!focus.stats.value"
            @click="saveFocusReportAs"
          >
            另存为…
          </button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="!focus.stats.value"
            @click="exportFocusJson"
          >
            导出 JSON
          </button>
          <span v-if="reportStatus" class="status">{{ reportStatus }}</span>
        </div>
        <template v-if="focus.stats.value && focus.stats.value.days.length > 0">
          <p class="status">{{ focusStatsSummary(focus.stats.value) }}</p>
          <div class="focus__chart" aria-label="按日专注时长">
            <div
              v-for="day in focus.stats.value.days"
              :key="'bar-' + day.date"
              class="focus__bar"
              :style="{ height: barHeight(day.actualSeconds) + 'px' }"
              :title="day.date + ' · ' + Math.round(day.actualSeconds / 60) + ' 分钟'"
            ></div>
          </div>
          <ul class="focus__days">
            <li v-for="day in focus.stats.value.days" :key="day.date" class="focus__day">
              <span>{{ day.date.slice(5) }}</span>
              <span>{{ Math.round(day.actualSeconds / 60) }} 分钟</span>
            </li>
          </ul>
        </template>
        <p v-else class="empty">完成一次专注后这里会显示统计。</p>
      </div>
    </section>

    <section
      v-if="signedIn && modules.visibility.value.calendar"
      id="calendar"
      class="card"
      aria-labelledby="calendar-title"
    >
      <div class="card__row">
        <h2 id="calendar-title" class="card__title">工作日历</h2>
        <div class="composer__row">
          <button class="button button--ghost" type="button" @click="calendar.previousMonth()">上一月</button>
          <select v-model.number="calendar.year.value" class="input input--narrow" aria-label="年份" @change="jumpCalendar">
            <option v-for="year in calendarYears" :key="year" :value="year">{{ year }} 年</option>
          </select>
          <select v-model.number="calendar.month.value" class="input input--narrow" aria-label="月份" @change="jumpCalendar">
            <option v-for="month in 12" :key="month" :value="month">{{ month }} 月</option>
          </select>
          <button class="button button--ghost" type="button" @click="calendar.nextMonth()">下一月</button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="!selectedCalendarDate"
            @click="clearCalendarOverride"
          >
            清除选中覆盖
          </button>
        </div>
      </div>
      <p class="card__hint">点击日期切换“工作日/休息日”覆盖；方向键移动（月末/月初跨月）、Home/End、PageUp/PageDown 切月；默认周一至周五为工作日。</p>
      <div class="composer__row">
        <button class="button button--ghost" type="button" @click="calendarImportOpen = !calendarImportOpen">
          批量导入
        </button>
        <span v-if="calendarImportStatus" class="status">{{ calendarImportStatus }}</span>
      </div>
      <div v-if="calendarImportOpen" class="composer__row">
        <textarea
          v-model="calendarImportText"
          class="input"
          rows="4"
          placeholder="每行：YYYY-MM-DD,holiday|workday[,备注]"
          aria-label="批量导入工作日覆盖"
        ></textarea>
        <button class="button" type="button" @click="importCalendarOverrides">导入</button>
      </div>
      <p v-if="calendar.error.value" class="status status--error" role="alert">{{ calendar.error.value }}</p>
      <div ref="calendarGrid" class="calendar-grid" role="group" aria-label="工作日历">
        <span v-for="label in ['一', '二', '三', '四', '五', '六', '日']" :key="label" class="calendar-weekday">{{ label }}</span>
        <span
          v-for="blank in monthLeadingBlanks(calendar.year.value, calendar.month.value)"
          :key="'blank-' + blank"
          class="calendar-blank"
        ></span>
        <button
          v-for="day in calendar.days.value"
          :key="day.date"
          class="calendar-day"
          :class="'calendar-day--' + day.kind"
          type="button"
          :data-date="day.date"
          :tabindex="tabbableDate(calendar.days.value, focusedCalendarDate) === day.date ? 0 : -1"
          :aria-label="day.date + (day.kind === 'workday' ? ' 工作日' : ' 休息日')"
          @focus="focusedCalendarDate = day.date"
          @click="toggleCalendarDay(day.date, day.kind)"
          @keydown.left.prevent="focusCalendarDay(day.date, -1)"
          @keydown.right.prevent="focusCalendarDay(day.date, 1)"
          @keydown.up.prevent="focusCalendarDay(day.date, -7)"
          @keydown.down.prevent="focusCalendarDay(day.date, 7)"
          @keydown.home.prevent="focusCalendarDay(day.date, -999)"
          @keydown.end.prevent="focusCalendarDay(day.date, 999)"
          @keydown.page-up.prevent="changeCalendarMonth(-1)"
          @keydown.page-down.prevent="changeCalendarMonth(1)"
        >
          {{ Number(day.date.slice(8)) }}
        </button>
      </div>
    </section>

    <section v-if="signedIn && modules.visibility.value.goals" id="goals" class="card" aria-labelledby="goals-title">
      <h2 id="goals-title" class="card__title">目标</h2>
      <form class="composer" @submit.prevent="submitGoal">
        <label class="composer__label" for="goal-title">新增目标</label>
        <div class="composer__row">
          <input
            id="goal-title"
            v-model="goalTitle"
            class="input"
            type="text"
            maxlength="200"
            placeholder="例如：完成第一次半马"
            autocomplete="off"
          />
          <input v-model="goalTargetDate" class="input input--narrow" type="date" aria-label="目标日期" />
          <button class="button" type="submit" :disabled="goals.loading.value || goalTitle.trim() === ''">
            创建
          </button>
        </div>
      </form>
      <div class="composer__row">
        <label class="habits__meta">
          <input v-model="showArchivedGoals" type="checkbox" />
          显示已归档目标
        </label>
      </div>
      <ul v-if="filterGoals(goals.goals.value, showArchivedGoals).length > 0" class="goals">
        <li v-for="goal in filterGoals(goals.goals.value, showArchivedGoals)" :key="goal.id" class="goals__item">
          <div v-if="editingGoal === goal.id" class="goals__edit">
            <input v-model="editGoalTitle" class="input" type="text" maxlength="200" aria-label="目标标题" />
            <input v-model="editGoalDescription" class="input" type="text" maxlength="2000" aria-label="目标描述" />
            <input v-model="editGoalTargetDate" class="input input--narrow" type="date" aria-label="目标日期" />
            <div class="composer__row">
              <button class="button" type="button" @click="saveEditGoal(goal.id)">保存</button>
              <button class="button button--ghost" type="button" @click="cancelEditGoal">取消</button>
            </div>
          </div>
          <div v-else class="goals__header">
            <span class="goals__title">{{ goal.title }}</span>
            <span class="goals__meta">
              {{ goal.status }} · {{ goal.progress.completedMilestones }}/{{ goal.progress.totalMilestones }}
              <template v-if="goal.targetDate"> · {{ goal.targetDate }}</template>
            </span>
            <button class="button button--ghost" type="button" @click="startEditGoal(goal.id)">编辑</button>
            <button class="button button--ghost" type="button" @click="requestDeleteGoal(goal.id)">删除</button>
          </div>
          <div class="goals__progress" :aria-label="`完成 ${goal.progress.percent}%`">
            <div class="goals__bar" :style="{ width: `${goal.progress.percent}%` }"></div>
          </div>
          <ul class="goals__milestones">
            <li v-for="milestone in goal.milestones" :key="milestone.id" class="goals__milestone">
              <label>
                <input
                  type="checkbox"
                  :checked="milestone.completed"
                  @change="toggleGoalMilestone(goal.id, milestone.id)"
                />
                {{ milestone.title }}
              </label>
              <button
                class="button button--ghost"
                type="button"
                :disabled="goals.loading.value"
                @click="goals.moveMilestone(goal.id, milestone, 'up')"
              >
                上移
              </button>
              <button
                class="button button--ghost"
                type="button"
                :disabled="goals.loading.value"
                @click="goals.moveMilestone(goal.id, milestone, 'down')"
              >
                下移
              </button>
              <button
                class="button button--ghost"
                type="button"
                @click="goals.removeMilestone(goal.id, milestone)"
              >
                删除
              </button>
            </li>
          </ul>
          <div
            v-if="['AI_UNAVAILABLE', 'RATE_LIMITED'].includes(goals.error.value ?? '')"
            class="composer__row"
          >
            <button
              class="button button--ghost"
              type="button"
              :disabled="aiRetryRemaining() > 0 || goals.breakdownLoading.value"
              @click="retryGoalBreakdown(goal.id)"
            >
              重试<span v-if="aiRetryRemaining() > 0">（{{ aiRetryRemaining() }}s）</span>
            </button>
          </div>
          <div class="composer__row">
            <input
              v-model="milestoneDrafts[goal.id]"
              class="input"
              type="text"
              maxlength="200"
              placeholder="新增里程碑"
              :aria-label="`为 ${goal.title} 新增里程碑`"
            />
            <button
              class="button button--ghost"
              type="button"
              :disabled="goals.loading.value"
              @click="submitMilestone(goal.id)"
            >
              添加
            </button>
            <button
              v-if="goal.status !== 'completed'"
              class="button button--ghost"
              type="button"
              @click="goals.setStatus(goal.id, 'completed')"
            >
              完成
            </button>
            <button
              v-if="goal.status !== 'archived'"
              class="button button--ghost"
              type="button"
              @click="goals.setStatus(goal.id, 'archived')"
            >
              归档
            </button>
          </div>
          <div class="composer__row">
            <input
              v-model="goalGuidance[goal.id]"
              class="input"
              type="text"
              maxlength="2000"
              placeholder="AI 拆解提示（可选，例如：调研, 原型, 上线）"
              :aria-label="`为 ${goal.title} 的 AI 拆解提示`"
            />
            <button
              class="button button--ghost"
              type="button"
              :disabled="goals.breakdownLoading.value"
              @click="previewGoalBreakdown(goal.id)"
            >
              AI 拆解
            </button>
          </div>
          <div v-if="goalSuggestions(goal.id).length > 0" class="goals__suggestions">
            <div class="goals__header">
              <span class="habits__meta">AI 建议（添加后才会写入）</span>
              <button
                class="button button--ghost"
                type="button"
                :disabled="goals.breakdownLoading.value"
                @click="addAllGoalSuggestions(goal.id)"
              >
                全部添加
              </button>
            </div>
            <ul class="goals__milestones">
              <li v-for="suggestion in goalSuggestions(goal.id)" :key="suggestion.position" class="goals__milestone">
                <span>{{ suggestion.title }}</span>
                <button
                  class="button button--ghost"
                  type="button"
                  :disabled="goals.breakdownLoading.value"
                  @click="addGoalSuggestion(goal.id, suggestion)"
                >
                  添加
                </button>
              </li>
            </ul>
          </div>
        </li>
      </ul>
      <p v-else class="empty">还没有目标，先创建一个可衡量的目标。</p>
    </section>

    <section v-if="signedIn && modules.visibility.value.habits" id="habits" class="card" aria-labelledby="habits-title">
      <h2 id="habits-title" class="card__title">习惯打卡</h2>
      <div class="habits__quiet">
        <span>免打扰</span>
        <input v-model="quietStart" class="input input--time" type="time" aria-label="免打扰开始" />
        <span>至</span>
        <input v-model="quietEnd" class="input input--time" type="time" aria-label="免打扰结束" />
        <span v-if="quietStart || quietEnd" class="status">此时段不发送提醒</span>
      </div>
      <p v-if="outbox.failed > 0" class="status status--error" role="alert">
        同步冲突：{{ outbox.failed }} 条变更未上传，请稍后重试
      </p>
      <div v-if="conflicts.length > 0" class="issues__header">
        <button class="button button--ghost" type="button" @click="exportLedger">导出冲突台账</button>
        <span v-if="ledgerStatus" class="status">{{ ledgerStatus }}</span>
      </div>
      <pre v-if="ledgerText" class="ledger">{{ ledgerText }}</pre>
      <ul v-if="conflicts.length > 0" class="issues" aria-label="版本冲突">
        <li v-for="conflict in conflicts" :key="conflict.mutationId" class="issues__item">
          <span class="issues__text">
            {{ conflict.entityType }} 版本冲突（服务器 v{{ conflict.serverVersion }}）
          </span>
          <span class="issues__actions">
            <button class="button button--ghost" type="button" @click="resolveConflictAction(conflict.mutationId, 'server')">
              以服务端为准
            </button>
            <button class="button button--ghost" type="button" @click="resolveConflictAction(conflict.mutationId, 'retry')">
              重试
            </button>
            <button class="button button--ghost" type="button" @click="resolveConflictAction(conflict.mutationId, 'overwrite')">
              覆盖
            </button>
          </span>
        </li>
      </ul>
      <ul v-if="issues.length > 0" class="issues" aria-label="待处理同步冲突">
        <li v-for="issue in issues" :key="issue.mutationId" class="issues__item">
          <span class="issues__text">
            {{ issue.entityType }} · {{ issue.operation }} · 已尝试 {{ issue.attempts }} 次
            <small v-if="issue.lastError">（{{ issue.lastError }}）</small>
          </span>
          <button
            class="button button--ghost"
            type="button"
            :disabled="habits.loading.value"
            @click="discardIssue(issue.mutationId)"
          >
            放弃本地变更
          </button>
        </li>
      </ul>
      <ul v-if="habits.habits.value.length > 0" class="habits">
        <li v-for="habit in habits.habits.value" :key="habit.id" class="habits__item">
          <template v-if="editingHabit === habit.id">
            <input v-model="editHabitTitle" class="input" type="text" maxlength="200" aria-label="习惯名称" />
            <select v-model="editHabitSchedule" class="input" aria-label="日程">
              <option value="daily">每日</option>
              <option value="weekdays">工作日</option>
              <option value="weekly">每周指定日</option>
            </select>
            <span v-if="editHabitSchedule === 'weekly'" class="habits__weekdays">
              <label v-for="day in [1, 2, 3, 4, 5, 6, 7]" :key="day">
                <input
                  type="checkbox"
                  :checked="editHabitWeekdays.includes(day)"
                  @change="toggleEditWeekday(day)"
                />
                {{ day }}
              </label>
            </span>
            <input v-model="editHabitTarget" class="input input--narrow" type="number" min="1" max="1000" aria-label="目标数量" />
            <input v-model="editHabitUnit" class="input input--narrow" type="text" maxlength="20" aria-label="单位" />
            <button class="button" type="button" :disabled="habits.loading.value" @click="saveEditHabit">保存</button>
            <button class="button button--ghost" type="button" @click="cancelEditHabit">取消</button>
          </template>
          <template v-else>
          <span class="habits__title">{{ habit.title }}</span>
          <span class="habits__meta">
            {{ scheduleLabel(habit) }} · {{ habit.targetQuantity }} {{ habit.unit }} · {{ habit.timeZone }}
          </span>
          <span class="habits__streak" aria-label="连续天数">
            连续 {{ habits.streaks.value[habit.id] ?? 0 }} 天
          </span>
          <span v-if="habit.pausedAt" class="habits__paused">已暂停</span>
          <span v-else-if="habits.todayLogs.value[habit.id]" class="habits__done">
            今日已打卡（v{{ habits.todayLogs.value[habit.id]?.version }}）
          </span>
          <button
            v-else
            class="button button--ghost"
            type="button"
            :disabled="habits.loading.value"
            @click="checkInHabit(habit.id)"
          >
            打卡
          </button>
          <label class="habits__reminder">
            <input
              type="time"
              class="input input--time"
              :value="reminderTime(habit.id)"
              aria-label="提醒时间"
              @change="setReminderTime(habit.id, $event)"
            />
            <input
              type="checkbox"
              :checked="reminderFor(habit.id).enabled"
              aria-label="启用提醒"
              @change="setReminderEnabled(habit.id, $event)"
            />
            提醒
          </label>
          <button
            class="button button--ghost"
            type="button"
            :disabled="habits.loading.value"
            @click="startEditHabit(habit.id)"
          >
            编辑
          </button>
          <button
            class="button button--ghost"
            type="button"
            :disabled="habits.loading.value"
            @click="toggleHabitPause(habit.id)"
          >
            {{ habit.pausedAt ? '恢复' : '暂停' }}
          </button>
          </template>
        </li>
      </ul>
      <p v-else class="empty">还没有习惯，先创建一个每日习惯。</p>
      <form class="composer" @submit.prevent="submitHabit">
        <label class="composer__label" for="habit-title">新增每日习惯</label>
        <div class="composer__row">
          <input
            id="habit-title"
            v-model="habitTitle"
            class="input"
            type="text"
            maxlength="200"
            placeholder="例如：阅读"
            autocomplete="off"
          />
          <input
            v-model="habitTarget"
            class="input input--narrow"
            type="number"
            min="1"
            max="1000"
            aria-label="目标数量"
          />
          <input
            v-model="habitUnit"
            class="input input--narrow"
            type="text"
            maxlength="20"
            aria-label="单位"
          />
          <button class="button" type="submit" :disabled="habits.loading.value || habitTitle.trim() === ''">
            创建
          </button>
        </div>
      </form>
    </section>

    <p v-if="errorText" class="status status--error" role="alert">{{ errorText }}</p>
  </main>
</template>
