import type { ModuleVisibility } from './modules'

export type Theme = 'auto' | 'light' | 'dark'

export function normalizeTheme(raw: string | null): Theme {
  return raw === 'light' || raw === 'dark' ? raw : 'auto'
}

export interface NavLink {
  id: string
  label: string
}

export function navLinks(visibility: ModuleVisibility): NavLink[] {
  const links: NavLink[] = [{ id: 'tasks', label: '任务' }]
  if (visibility.focus) links.push({ id: 'focus', label: '专注' })
  if (visibility.goals) links.push({ id: 'goals', label: '目标' })
  if (visibility.habits) links.push({ id: 'habits', label: '习惯' })
  if (visibility.notes) links.push({ id: 'notes', label: '笔记' })
  return links
}

export function densityClass(compact: boolean): string {
  return compact ? 'app--compact' : ''
}
