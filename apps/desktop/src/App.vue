<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { createBridge } from './api'
import { createSessionStore } from './session'
import { createHabitStore } from './habits'
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
    if (await session.sync()) await tasks.refresh()
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

onMounted(async () => {
  if (await session.restore()) {
    await tasks.refresh()
    await habits.refresh()
  }
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
      <ul v-if="habits.habits.value.length > 0" class="habits">
        <li v-for="habit in habits.habits.value" :key="habit.id" class="habits__item">
          <span class="habits__title">{{ habit.title }}</span>
          <span class="habits__meta">{{ habit.targetQuantity }} {{ habit.unit }}</span>
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
          <button
            class="button button--ghost"
            type="button"
            :disabled="habits.loading.value"
            @click="toggleHabitPause(habit.id)"
          >
            {{ habit.pausedAt ? '恢复' : '暂停' }}
          </button>
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
