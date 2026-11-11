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

/** The shell boundary: the UI only talks to this interface, never to SQLite or HTTP directly. */
export interface DesktopBridge {
  listTasks(listId: string): Promise<Task[]>
  createTask(input: CreateTaskInput): Promise<Task>
}

/** Deterministic in-memory bridge for `vite dev` in a browser and unit tests. */
export class MemoryBridge implements DesktopBridge {
  private readonly tasks = new Map<string, Task>()

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
}

type Invoke = <T>(command: string, args?: Record<string, unknown>) => Promise<T>

/** The Tauri shell exposes the same operations as commands backed by the Rust core store. */
export class TauriBridge implements DesktopBridge {
  constructor(private readonly invoke: Invoke) {}

  async listTasks(listId: string): Promise<Task[]> {
    const rows = await this.invoke<Array<Omit<Task, 'listId'> & { list_id: string }>>('list_tasks', {
      listId
    })
    return rows.map((row) => ({
      id: row.id,
      listId: row.list_id,
      title: row.title,
      status: row.status,
      version: row.version
    }))
  }

  async createTask(input: CreateTaskInput): Promise<Task> {
    const row = await this.invoke<Omit<Task, 'listId'> & { list_id: string }>('create_task', {
      id: input.id,
      listId: input.listId,
      title: input.title
    })
    return {
      id: row.id,
      listId: row.list_id,
      title: row.title,
      status: row.status,
      version: row.version
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
