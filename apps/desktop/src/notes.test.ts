import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge } from './api'
import { createNoteStore } from './notes'

function createStore(token = 'token') {
  const bridge = new MemoryBridge()
  const store = createNoteStore(bridge, {
    baseUrl: () => 'http://127.0.0.1:5080',
    token: () => token
  })
  return { bridge, store }
}

describe('note store', () => {
  it('creates notes and lists them newest first', async () => {
    const { store } = createStore()
    const first = await store.create('Meeting', '- agenda')
    expect(first?.title).toBe('Meeting')
    const second = await store.create('', 'quick thought')
    expect(second?.content).toBe('quick thought')
    await store.refresh()
    expect(store.notes.value).toHaveLength(2)
    expect(store.notes.value.map((note) => note.id)).toContain(first!.id)
  })

  it('rejects empty notes and missing tokens', async () => {
    const { store } = createStore()
    expect(await store.create('   ', '   ')).toBeNull()
    expect(store.error.value).toBe('INVALID_NOTE')

    const signedOut = createStore('')
    expect(await signedOut.store.create('Note', 'body')).toBeNull()
    expect(signedOut.store.error.value).toBe('NOTE_SIGNED_OUT')
  })

  it('links a note to a task on create', async () => {
    const { store } = createStore()
    const note = await store.create('Task context', 'details', 'task-1')
    expect(note?.taskId).toBe('task-1')
    await store.refresh()
    expect(store.notes.value[0]?.taskId).toBe('task-1')
  })

  it('filters by query on refresh', async () => {
    const { store } = createStore()
    await store.create('Meeting', 'agenda for Monday')
    await store.create('Shopping', 'milk and eggs')
    store.query.value = 'monday'
    await store.refresh()
    expect(store.notes.value).toHaveLength(1)
    expect(store.notes.value[0]?.title).toBe('Meeting')
    store.query.value = ''
    await store.refresh()
    expect(store.notes.value).toHaveLength(2)
  })

  it('updates content with the current version and deletes by version', async () => {
    const { store } = createStore()
    const note = await store.create('Draft', 'v1')
    expect(await store.update(note!.id, note!.version, 'v2 content')).toBe(true)
    expect(store.notes.value[0]?.content).toBe('v2 content')
    expect(store.notes.value[0]?.version).toBe(2)

    expect(await store.remove(store.notes.value[0]!)).toBe(true)
    expect(store.notes.value).toHaveLength(0)
  })

  it('maps version conflicts and invalid updates', async () => {
    const { store } = createStore()
    const note = await store.create('Draft', 'v1')
    await store.update(note!.id, note!.version, 'v2')
    expect(await store.update(note!.id, note!.version, 'stale')).toBe(false)
    expect(store.error.value).toBe('VERSION_CONFLICT')

    const bridge = new MemoryBridge()
    const created = await bridge.noteCreate('', 'token', { content: 'x' })
    const failing: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'noteDelete') {
          return async () => {
            throw new Error('409:VERSION_CONFLICT')
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const failingStore = createNoteStore(failing, { baseUrl: () => '', token: () => 'token' })
    await failingStore.refresh()
    expect(await failingStore.remove(created)).toBe(false)
    expect(failingStore.error.value).toBe('VERSION_CONFLICT')
  })
})
