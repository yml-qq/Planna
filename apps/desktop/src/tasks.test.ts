import { describe, expect, it, vi } from 'vitest'
import { MemoryBridge, type DesktopBridge, type Task } from './api'
import { createTaskStore } from './tasks'

function fixedIds(...ids: string[]): () => string {
  let index = 0
  return () => {
    const id = ids[index]
    index += 1
    if (!id) throw new Error('test ran out of ids')
    return id
  }
}

describe('MemoryBridge', () => {
  it('lists tasks of a single list sorted by title', async () => {
    const bridge = new MemoryBridge()
    await bridge.createTask({ id: 'a', listId: 'inbox', title: 'Zeta' })
    await bridge.createTask({ id: 'b', listId: 'inbox', title: 'Alpha' })
    await bridge.createTask({ id: 'c', listId: 'other', title: 'Hidden' })
    const tasks = await bridge.listTasks('inbox')
    expect(tasks.map((task) => task.title)).toEqual(['Alpha', 'Zeta'])
  })

  it('is idempotent for the same local id and rejects invalid titles', async () => {
    const bridge = new MemoryBridge()
    const first = await bridge.createTask({ id: 'a', listId: 'inbox', title: 'Task' })
    const replay = await bridge.createTask({ id: 'a', listId: 'inbox', title: 'Changed' })
    expect(replay).toEqual(first)
    await expect(bridge.createTask({ id: 'b', listId: 'inbox', title: '   ' })).rejects.toThrow(
      'INVALID_TITLE'
    )
  })
})

describe('task store', () => {
  it('refreshes from the bridge and prepends new tasks', async () => {
    const bridge = new MemoryBridge()
    await bridge.createTask({ id: 'existing', listId: 'inbox', title: 'Existing' })
    const store = createTaskStore(bridge, 'inbox', fixedIds('new'))
    await store.refresh()
    expect(store.tasks.value.map((task) => task.id)).toEqual(['existing'])

    const created = await store.add('  New task  ')
    expect(created?.title).toBe('New task')
    expect(store.tasks.value.map((task) => task.id)).toEqual(['new', 'existing'])
    expect(store.error.value).toBeNull()
    expect(store.loading.value).toBe(false)
  })

  it('rejects blank and oversized titles without calling the bridge', async () => {
    const bridge = new MemoryBridge()
    const createTask = vi.spyOn(bridge, 'createTask')
    const store = createTaskStore(bridge, 'inbox')
    expect(await store.add('   ')).toBeNull()
    expect(store.error.value).toBe('INVALID_TITLE')
    expect(await store.add('a'.repeat(501))).toBeNull()
    expect(createTask).not.toHaveBeenCalled()
  })

  it('reports save and load failures without corrupting the list', async () => {
    const failing: DesktopBridge = {
      listTasks: async () => {
        throw new Error('offline')
      },
      createTask: async () => {
        throw new Error('offline')
      },
      requestLoginCode: async () => {
        throw new Error('offline')
      },
      login: async () => {
        throw new Error('offline')
      },
      restoreSession: async () => {
        throw new Error('offline')
      },
      signOut: async () => {
        throw new Error('offline')
      },
      runSync: async () => {
        throw new Error('offline')
      },
      listHabits: async () => {
        throw new Error('offline')
      },
      createHabit: async () => {
        throw new Error('offline')
      },
      listHabitLogs: async () => {
        throw new Error('offline')
      },
      upsertHabitLog: async () => {
        throw new Error('offline')
      },
      updateHabit: async () => {
        throw new Error('offline')
      },
      outboxStatus: async () => ({ pending: 0, failed: 0 })
    }
    const store = createTaskStore(failing, 'inbox')
    await store.refresh()
    expect(store.error.value).toBe('LOAD_FAILED')
    expect(store.tasks.value).toEqual([])
    expect(await store.add('Task')).toBeNull()
    expect(store.error.value).toBe('SAVE_FAILED')
  })

  it('replaces an existing task instead of duplicating it', async () => {
    const bridge = new MemoryBridge()
    const store = createTaskStore(bridge, 'inbox', fixedIds('same', 'same'))
    await store.add('First')
    const replacement: Task = {
      id: 'same',
      listId: 'inbox',
      title: 'First',
      status: 'todo',
      version: 1
    }
    store.tasks.value = [replacement]
    await store.add('Second')
    expect(store.tasks.value).toEqual([replacement])
  })
})
