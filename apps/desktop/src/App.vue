<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { createBridge } from './api'
import { createTaskStore } from './tasks'

const store = createTaskStore(createBridge())
const title = ref('')
const submitting = ref(false)

async function submit(): Promise<void> {
  if (submitting.value) return
  submitting.value = true
  try {
    const created = await store.add(title.value)
    if (created) title.value = ''
  } finally {
    submitting.value = false
  }
}

onMounted(() => {
  void store.refresh()
})
</script>

<template>
  <main class="app">
    <header class="app__header">
      <h1>Planna 普拉清单</h1>
      <p class="app__subtitle">本地优先 · 离线可用</p>
    </header>

    <form class="composer" @submit.prevent="submit">
      <label class="composer__label" for="task-title">新增任务</label>
      <div class="composer__row">
        <input
          id="task-title"
          v-model="title"
          class="composer__input"
          type="text"
          maxlength="500"
          placeholder="例如：写周报"
          autocomplete="off"
        />
        <button class="composer__button" type="submit" :disabled="submitting || title.trim() === ''">
          添加
        </button>
      </div>
    </form>

    <p v-if="store.error.value" class="status status--error" role="alert">
      {{ store.error.value === 'INVALID_TITLE' ? '标题不能为空且不超过 500 字' : '操作失败，请重试' }}
    </p>
    <p v-else-if="store.loading.value" class="status">加载中…</p>

    <ul v-if="store.tasks.value.length > 0" class="tasks" aria-label="任务列表">
      <li v-for="task in store.tasks.value" :key="task.id" class="tasks__item">
        <span class="tasks__title">{{ task.title }}</span>
        <span class="tasks__meta">v{{ task.version }}</span>
      </li>
    </ul>
    <p v-else-if="!store.loading.value" class="empty">还没有任务，先在下方添加一条。</p>
  </main>
</template>
