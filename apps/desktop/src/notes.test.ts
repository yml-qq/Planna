import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge } from './api'
import {
  attachmentErrorMessage,
  canPreviewAttachment,
  createNoteStore,
  isPdfAttachment,
  isTextAttachment,
  notesForTask,
  retryAfterSeconds
} from './notes'

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

  it('updates title and content with the current version and deletes by version', async () => {
    const { store } = createStore()
    const note = await store.create('Draft', 'v1')
    expect(await store.update(note!.id, note!.version, { title: 'Final', content: 'v2 content' })).toBe(true)
    expect(store.notes.value[0]?.title).toBe('Final')
    expect(store.notes.value[0]?.content).toBe('v2 content')
    expect(store.notes.value[0]?.version).toBe(2)
    expect(await store.update(note!.id, 2, { title: '   ', content: '' })).toBe(false)
    expect(store.error.value).toBe('INVALID_NOTE')

    expect(await store.remove(store.notes.value[0]!)).toBe(true)
    expect(store.notes.value).toHaveLength(0)
  })

  it('maps version conflicts and invalid updates', async () => {
    const { store } = createStore()
    const note = await store.create('Draft', 'v1')
    await store.update(note!.id, note!.version, { content: 'v2' })
    expect(await store.update(note!.id, note!.version, { content: 'stale' })).toBe(false)
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

describe('notes for task', () => {
  it('filters notes by task id', () => {
    const notes = [
      { id: 'n1', title: 'A', content: 'a', taskId: 'task-1', version: 1, createdAt: '', updatedAt: '' },
      { id: 'n2', title: 'B', content: 'b', taskId: null, version: 1, createdAt: '', updatedAt: '' },
      { id: 'n3', title: 'C', content: 'c', taskId: 'task-1', version: 1, createdAt: '', updatedAt: '' }
    ]
    expect(notesForTask(notes, 'task-1').map((note) => note.id)).toEqual(['n1', 'n3'])
    expect(notesForTask(notes, 'missing')).toEqual([])
  })
})

describe('note attachments bridge', () => {
  it('lists, uploads, downloads and deletes attachments', async () => {
    const { bridge } = createStore()
    const attachment = await bridge.noteAttachmentUpload('', 'token', 'note-1', '/tmp/report.pdf')
    expect(attachment.contentType).toBe('application/pdf')
    expect(attachment.noteId).toBe('note-1')
    expect(await bridge.noteAttachmentList('', 'token', 'note-1')).toHaveLength(1)
    expect(await bridge.noteAttachmentDownload('', 'token', 'note-1', attachment.id)).toBe(
      'downloads/' + attachment.id
    )
    await bridge.noteAttachmentDelete('', 'token', 'note-1', attachment.id)
    expect(await bridge.noteAttachmentList('', 'token', 'note-1')).toEqual([])
    await expect(
      bridge.noteAttachmentUpload('', 'token', 'note-1', '/tmp/bad.exe')
    ).rejects.toThrow('UNSUPPORTED_ATTACHMENT_TYPE')
  })
})

describe('note attachment store', () => {
  it('picks, uploads, downloads and deletes through the store', async () => {
    const { store } = createStore()
    expect(await store.pickAttachment()).toBe('/tmp/note.txt')
    expect(await store.uploadAttachment('note-1', '/tmp/report.pdf')).toBe(true)
    await store.loadAttachments('note-1')
    const items = store.attachments.value['note-1'] ?? []
    expect(items).toHaveLength(1)
    expect(items[0]?.contentType).toBe('application/pdf')
    expect(await store.downloadAttachment('note-1', items[0]!.id)).toBe('downloads/' + items[0]!.id)
    expect(await store.deleteAttachment('note-1', items[0]!.id)).toBe(true)
    expect(store.attachments.value['note-1']).toEqual([])
  })
})

describe('attachment retry countdown', () => {
  it('parses rate-limit seconds with a safe fallback', () => {
    expect(retryAfterSeconds('ATTACHMENT_RATE_LIMITED:42')).toBe(42)
    expect(retryAfterSeconds('ATTACHMENT_RATE_LIMITED')).toBe(60)
    expect(retryAfterSeconds('ATTACHMENT_RATE_LIMITED:0')).toBe(60)
    expect(retryAfterSeconds('SOMETHING_ELSE:10')).toBe(0)
    expect(retryAfterSeconds(null)).toBe(0)
  })
})

describe('attachment preview support', () => {
  it('only previews supported image types', () => {
    expect(canPreviewAttachment('image/png')).toBe(true)
    expect(canPreviewAttachment('image/jpeg')).toBe(true)
    expect(canPreviewAttachment('application/pdf')).toBe(true)
    expect(isPdfAttachment('application/pdf')).toBe(true)
    expect(isPdfAttachment('image/png')).toBe(false)
    expect(canPreviewAttachment('text/plain')).toBe(true)
    expect(canPreviewAttachment('text/markdown')).toBe(true)
    expect(isTextAttachment('text/plain')).toBe(true)
    expect(isTextAttachment('image/png')).toBe(false)
  })
})

describe('attachment error messages', () => {
  it('formats rate limits with retry seconds and other codes', () => {
    expect(attachmentErrorMessage('ATTACHMENT_RATE_LIMITED:42')).toBe('上传过于频繁，请 42 秒后重试')
    expect(attachmentErrorMessage('ATTACHMENT_RATE_LIMITED')).toBe('上传过于频繁，请稍后重试')
    expect(attachmentErrorMessage('UNSUPPORTED_ATTACHMENT_TYPE')).toBe('不支持该文件类型')
    expect(attachmentErrorMessage(null)).toBe('')
    expect(attachmentErrorMessage('SOMETHING_ELSE')).toBe('附件操作失败（SOMETHING_ELSE）')
  })
})
