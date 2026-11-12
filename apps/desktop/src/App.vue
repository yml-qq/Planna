<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { createBridge, type ConflictIssue, type ConflictStrategy, type OutboxIssue } from './api'
import { createSessionStore } from './session'
import { createHabitStore } from './habits'
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
const sentReminders = new Set<string>()
const notifierPromise = createNotifier()
let reminderTimer: ReturnType<typeof setInterval> | null = null

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
  const error = session.error.value ?? tasks.error.value
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

async function resolveConflictAction(mutationId: string, strategy: ConflictStrategy): Promise<void> {
  await bridge.resolveConflict(mutationId, strategy)
  await refreshOutbox()
  await tasks.refresh()
  await habits.refresh()
}

async function discardIssue(mutationId: string): Promise<void> {
  await bridge.discardMutation(mutationId)
  await refreshOutbox()
  await habits.refresh()
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

watch([quietStart, quietEnd], ([start, end]) => {
  if (start === '' && end === '') {
    writeStorage('planna.quietHours', '')
    return
  }
  writeStorage('planna.quietHours', JSON.stringify({ start, end }))
})

onUnmounted(() => {
  if (reminderTimer) clearInterval(reminderTimer)
})

onMounted(async () => {
  if (await session.restore()) {
    await tasks.refresh()
    await habits.refresh()
    await refreshOutbox()
  }
  reminderTimer = setInterval(() => {
    void tickReminders()
  }, 30_000)
})
</script>

<template>
  <main class="app">
    <header class="app__header">
      <h1>Planna 普拉清单</h1>
      <p class="app__subtitle">本地优先 · 离线可用 · 登录后同步</p>
    </header>

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
      </section>

      <form class="composer" @submit.prevent="submitTask">
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
          <span class="tasks__title">{{ task.title }}</span>
          <span class="tasks__meta">v{{ task.version }}</span>
        </li>
      </ul>
      <p v-else-if="!tasks.loading.value" class="empty">还没有任务，先添加一条。</p>
    </template>

    <section v-if="signedIn" class="card" aria-labelledby="habits-title">
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
