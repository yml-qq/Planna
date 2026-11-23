import { ref, type Ref } from 'vue'
import type { DesktopBridge, Note, NoteAttachment } from './api'

export function attachmentErrorMessage(code: string | null): string {
  if (!code) return ''
  const [base, seconds] = code.split(':')
  switch (base) {
    case 'ATTACHMENT_RATE_LIMITED':
      return seconds ? `上传过于频繁，请 ${seconds} 秒后重试` : '上传过于频繁，请稍后重试'
    case 'UNSUPPORTED_ATTACHMENT_TYPE':
      return '不支持该文件类型'
    case 'ATTACHMENT_TOO_LARGE':
      return '文件超过大小限制'
    case 'ATTACHMENT_EMPTY':
      return '文件为空'
    case 'ATTACHMENT_NOTE_QUOTA':
      return '该笔记附件数量已达上限'
    case 'ATTACHMENT_OWNER_QUOTA':
      return '附件总容量已达上限'
    case 'NOTE_NOT_FOUND':
      return '笔记尚未同步到服务端，请先同步'
    default:
      return `附件操作失败（${code}）`
  }
}

export function notesForTask(notes: Note[], taskId: string): Note[] {
  return notes.filter((note) => note.taskId === taskId)
}

export interface NoteStoreOptions {
  baseUrl: () => string
  token: () => string | null
}

export interface NoteStore {
  notes: Ref<Note[]>
  query: Ref<string>
  loading: Ref<boolean>
  error: Ref<string | null>
  refresh: () => Promise<void>
  create: (title: string, content: string, taskId?: string | null) => Promise<Note | null>
  update: (
    noteId: string,
    expectedVersion: number,
    patch: { title?: string; content?: string }
  ) => Promise<boolean>
  remove: (note: Note) => Promise<boolean>
  attachments: Ref<Record<string, NoteAttachment[]>>
  attachmentError: Ref<string | null>
  loadAttachments: (noteId: string) => Promise<void>
  pickAttachment: () => Promise<string | null>
  uploadAttachment: (noteId: string, path: string) => Promise<boolean>
  downloadAttachment: (noteId: string, attachmentId: string) => Promise<string | null>
  deleteAttachment: (noteId: string, attachmentId: string) => Promise<boolean>
}

function errorCode(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error)
  const separator = message.indexOf(':')
  return separator >= 0 ? message.slice(separator + 1) : 'NOTE_FAILED'
}

export function createNoteStore(bridge: DesktopBridge, options: NoteStoreOptions): NoteStore {
  const notes = ref<Note[]>([])
  const query = ref('')
  const loading = ref(false)
  const error = ref<string | null>(null)
  const attachments = ref<Record<string, NoteAttachment[]>>({})
  const attachmentError = ref<string | null>(null)

  function tokenOrNull(): string | null {
    const token = options.token()
    if (!token) {
      error.value = 'NOTE_SIGNED_OUT'
      return null
    }
    return token
  }

  async function refresh(): Promise<void> {
    const token = tokenOrNull()
    if (!token) return
    loading.value = true
    error.value = null
    try {
      notes.value = await bridge.noteList(options.baseUrl(), token, { query: query.value })
    } catch (failure) {
      error.value = errorCode(failure)
    } finally {
      loading.value = false
    }
  }

  async function create(
    title: string,
    content: string,
    taskId: string | null = null
  ): Promise<Note | null> {
    const trimmedTitle = title.trim()
    if (trimmedTitle.length > 200 || content.length > 20000 ||
        (trimmedTitle.length === 0 && content.trim().length === 0)) {
      error.value = 'INVALID_NOTE'
      return null
    }
    const token = tokenOrNull()
    if (!token) return null
    loading.value = true
    error.value = null
    try {
      const note = await bridge.noteCreate(options.baseUrl(), token, {
        title: trimmedTitle,
        content,
        taskId
      })
      notes.value = [note, ...notes.value]
      return note
    } catch (failure) {
      error.value = errorCode(failure)
      return null
    } finally {
      loading.value = false
    }
  }

  async function update(
    noteId: string,
    expectedVersion: number,
    patch: { title?: string; content?: string }
  ): Promise<boolean> {
    const note = notes.value.find((candidate) => candidate.id === noteId)
    const title = patch.title === undefined ? note?.title ?? '' : patch.title.trim()
    const content = patch.content === undefined ? note?.content ?? '' : patch.content
    if (title.length > 200 || content.length > 20000 ||
        (title.length === 0 && content.trim().length === 0)) {
      error.value = 'INVALID_NOTE'
      return false
    }
    const token = tokenOrNull()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      const updated = await bridge.noteUpdate(options.baseUrl(), token, noteId, expectedVersion, {
        title,
        content
      })
      notes.value = notes.value.map((note) => (note.id === noteId ? updated : note))
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function remove(note: Note): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      await bridge.noteDelete(options.baseUrl(), token, note.id, note.version)
      notes.value = notes.value.filter((candidate) => candidate.id !== note.id)
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function loadAttachments(noteId: string): Promise<void> {
    const token = tokenOrNull()
    if (!token) return
    attachmentError.value = null
    try {
      const items = await bridge.noteAttachmentList(options.baseUrl(), token, noteId)
      attachments.value = { ...attachments.value, [noteId]: items }
    } catch (failure) {
      attachmentError.value = errorCode(failure)
    }
  }

  async function pickAttachment(): Promise<string | null> {
    try {
      return await bridge.pickNoteAttachment()
    } catch (failure) {
      attachmentError.value = errorCode(failure)
      return null
    }
  }

  async function uploadAttachment(noteId: string, path: string): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    attachmentError.value = null
    try {
      const created = await bridge.noteAttachmentUpload(options.baseUrl(), token, noteId, path)
      attachments.value = {
        ...attachments.value,
        [noteId]: [...(attachments.value[noteId] ?? []), created]
      }
      return true
    } catch (failure) {
      attachmentError.value = errorCode(failure)
      return false
    }
  }

  async function downloadAttachment(noteId: string, attachmentId: string): Promise<string | null> {
    const token = tokenOrNull()
    if (!token) return null
    attachmentError.value = null
    try {
      return await bridge.noteAttachmentDownload(options.baseUrl(), token, noteId, attachmentId)
    } catch (failure) {
      attachmentError.value = errorCode(failure)
      return null
    }
  }

  async function deleteAttachment(noteId: string, attachmentId: string): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    attachmentError.value = null
    try {
      await bridge.noteAttachmentDelete(options.baseUrl(), token, noteId, attachmentId)
      attachments.value = {
        ...attachments.value,
        [noteId]: (attachments.value[noteId] ?? []).filter((item) => item.id !== attachmentId)
      }
      return true
    } catch (failure) {
      attachmentError.value = errorCode(failure)
      return false
    }
  }

  return {
    notes,
    query,
    loading,
    error,
    refresh,
    create,
    update,
    remove,
    attachments,
    attachmentError,
    loadAttachments,
    pickAttachment,
    uploadAttachment,
    downloadAttachment,
    deleteAttachment
  }
}
