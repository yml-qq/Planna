import { ref, type Ref } from 'vue'
import type {
  DesktopBridge,
  Goal,
  GoalBreakdownSuggestion,
  GoalState,
  Milestone
} from './api'

export interface GoalStoreOptions {
  baseUrl: () => string
  token: () => string | null
}

export interface GoalStore {
  goals: Ref<Goal[]>
  loading: Ref<boolean>
  error: Ref<string | null>
  refresh: () => Promise<void>
  create: (title: string, targetDate?: string | null) => Promise<Goal | null>
  setStatus: (goalId: string, status: GoalState) => Promise<boolean>
  addMilestone: (goalId: string, title: string) => Promise<boolean>
  toggleMilestone: (goalId: string, milestone: Milestone) => Promise<boolean>
  suggestions: Ref<Record<string, GoalBreakdownSuggestion[]>>
  breakdownLoading: Ref<boolean>
  previewBreakdown: (goalId: string, guidance?: string) => Promise<boolean>
  addSuggestion: (goalId: string, suggestion: GoalBreakdownSuggestion) => Promise<boolean>
  addAllSuggestions: (goalId: string) => Promise<number>
}

function errorCode(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error)
  const separator = message.indexOf(':')
  return separator >= 0 ? message.slice(separator + 1) : 'GOAL_FAILED'
}

export function createGoalStore(bridge: DesktopBridge, options: GoalStoreOptions): GoalStore {
  const goals = ref<Goal[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  const suggestions = ref<Record<string, GoalBreakdownSuggestion[]>>({})
  const breakdownLoading = ref(false)

  function tokenOrNull(): string | null {
    const token = options.token()
    if (!token) {
      error.value = 'GOAL_SIGNED_OUT'
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
      goals.value = await bridge.goalList(options.baseUrl(), token)
    } catch (failure) {
      error.value = errorCode(failure)
    } finally {
      loading.value = false
    }
  }

  async function create(title: string, targetDate?: string | null): Promise<Goal | null> {
    const trimmed = title.trim()
    if (trimmed.length === 0 || trimmed.length > 200) {
      error.value = 'INVALID_GOAL'
      return null
    }
    const token = tokenOrNull()
    if (!token) return null
    loading.value = true
    error.value = null
    try {
      const goal = await bridge.goalCreate(options.baseUrl(), token, {
        title: trimmed,
        targetDate: targetDate === '' ? null : targetDate ?? null
      })
      goals.value = [goal, ...goals.value]
      return goal
    } catch (failure) {
      error.value = errorCode(failure)
      return null
    } finally {
      loading.value = false
    }
  }

  async function setStatus(goalId: string, status: GoalState): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    const goal = goals.value.find((candidate) => candidate.id === goalId)
    if (!goal) {
      error.value = 'GOAL_NOT_FOUND'
      return false
    }
    loading.value = true
    error.value = null
    try {
      const updated = await bridge.goalUpdate(options.baseUrl(), token, goalId, goal.version, {
        status
      })
      goals.value = goals.value.map((candidate) => (candidate.id === goalId ? updated : candidate))
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function addMilestone(goalId: string, title: string): Promise<boolean> {
    const trimmed = title.trim()
    if (trimmed.length === 0 || trimmed.length > 200) {
      error.value = 'INVALID_GOAL'
      return false
    }
    const token = tokenOrNull()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      await bridge.goalAddMilestone(options.baseUrl(), token, goalId, { title: trimmed })
      await refresh()
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function toggleMilestone(goalId: string, milestone: Milestone): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    loading.value = true
    error.value = null
    try {
      await bridge.goalUpdateMilestone(
        options.baseUrl(),
        token,
        goalId,
        milestone.id,
        milestone.version,
        { completed: !milestone.completed }
      )
      await refresh()
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      loading.value = false
    }
  }

  async function previewBreakdown(goalId: string, guidance?: string): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    breakdownLoading.value = true
    error.value = null
    try {
      const items = await bridge.goalBreakdownPreview(options.baseUrl(), token, goalId, guidance)
      suggestions.value = { ...suggestions.value, [goalId]: items }
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      breakdownLoading.value = false
    }
  }

  async function addSuggestion(goalId: string, suggestion: GoalBreakdownSuggestion): Promise<boolean> {
    const token = tokenOrNull()
    if (!token) return false
    breakdownLoading.value = true
    error.value = null
    try {
      await bridge.goalAddMilestone(options.baseUrl(), token, goalId, { title: suggestion.title })
      suggestions.value = {
        ...suggestions.value,
        [goalId]: (suggestions.value[goalId] ?? []).filter((item) => item !== suggestion)
      }
      await refresh()
      return true
    } catch (failure) {
      error.value = errorCode(failure)
      return false
    } finally {
      breakdownLoading.value = false
    }
  }

  async function addAllSuggestions(goalId: string): Promise<number> {
    const items = suggestions.value[goalId] ?? []
    let added = 0
    for (const item of [...items]) {
      if (await addSuggestion(goalId, item)) added += 1
      else break
    }
    return added
  }

  return {
    goals,
    loading,
    error,
    refresh,
    create,
    setStatus,
    addMilestone,
    toggleMilestone,
    suggestions,
    breakdownLoading,
    previewBreakdown,
    addSuggestion,
    addAllSuggestions
  }
}
