import { ref, type Ref } from 'vue'
import type { DesktopBridge, Task } from './api'

export interface TaskStore {
  tasks: Ref<Task[]>
  loading: Ref<boolean>
  error: Ref<string | null>
  refresh: () => Promise<void>
  add: (title: string) => Promise<Task | null>
}

export function createTaskStore(
  bridge: DesktopBridge,
  listId = 'list-inbox',
  newId: () => string = () => crypto.randomUUID()
): TaskStore {
  const tasks = ref<Task[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function refresh(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      tasks.value = await bridge.listTasks(listId)
    } catch {
      error.value = 'LOAD_FAILED'
    } finally {
      loading.value = false
    }
  }

  async function add(title: string): Promise<Task | null> {
    const trimmed = title.trim()
    if (trimmed.length === 0 || trimmed.length > 500) {
      error.value = 'INVALID_TITLE'
      return null
    }
    loading.value = true
    error.value = null
    try {
      const task = await bridge.createTask({ id: newId(), listId, title: trimmed })
      tasks.value = [task, ...tasks.value.filter((existing) => existing.id !== task.id)]
      return task
    } catch {
      error.value = 'SAVE_FAILED'
      return null
    } finally {
      loading.value = false
    }
  }

  return { tasks, loading, error, refresh, add }
}
