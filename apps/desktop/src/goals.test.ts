import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge } from './api'
import { createGoalStore, filterGoals } from './goals'

function createStore(token = 'token') {
  const bridge = new MemoryBridge()
  const store = createGoalStore(bridge, {
    baseUrl: () => 'http://127.0.0.1:5080',
    token: () => token
  })
  return { bridge, store }
}

describe('goal store', () => {
  it('creates a goal and lists it', async () => {
    const { store } = createStore()
    const goal = await store.create('  Ship v1  ', '2026-12-31')
    expect(goal?.title).toBe('Ship v1')
    expect(goal?.targetDate).toBe('2026-12-31')
    expect(goal?.status).toBe('active')
    await store.refresh()
    expect(store.goals.value).toHaveLength(1)
    expect(store.goals.value[0]?.progress).toEqual({
      completedMilestones: 0,
      totalMilestones: 0,
      percent: 0
    })
  })

  it('rejects invalid titles and missing tokens', async () => {
    const { store } = createStore()
    expect(await store.create('   ')).toBeNull()
    expect(store.error.value).toBe('INVALID_GOAL')

    const signedOut = createStore('')
    expect(await signedOut.store.create('Ship')).toBeNull()
    expect(signedOut.store.error.value).toBe('GOAL_SIGNED_OUT')
  })

  it('adds milestones and recomputes progress after toggling', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    expect(await store.addMilestone(goal!.id, 'Draft')).toBe(true)
    expect(await store.addMilestone(goal!.id, 'Review')).toBe(true)
    let current = store.goals.value[0]!
    expect(current.milestones).toHaveLength(2)
    expect(current.progress.percent).toBe(0)

    expect(await store.toggleMilestone(current.id, current.milestones[0]!)).toBe(true)
    current = store.goals.value[0]!
    expect(current.progress).toEqual({
      completedMilestones: 1,
      totalMilestones: 2,
      percent: 50
    })

    expect(await store.toggleMilestone(current.id, current.milestones[0]!)).toBe(true)
    current = store.goals.value[0]!
    expect(current.progress.percent).toBe(0)
  })

  it('updates status with the current version', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    expect(await store.setStatus(goal!.id, 'completed')).toBe(true)
    expect(store.goals.value[0]?.status).toBe('completed')
    expect(store.goals.value[0]?.version).toBe(2)
    expect(await store.setStatus('missing', 'archived')).toBe(false)
    expect(store.error.value).toBe('GOAL_NOT_FOUND')
  })

  it('maps version conflicts from the bridge', async () => {
    const bridge = new MemoryBridge()
    const goal = await bridge.goalCreate('', 'token', { title: 'Ship' })
    await bridge.goalUpdate('', 'token', goal.id, goal.version, { title: 'Renamed' })
    const failing: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'goalUpdate') {
          return async () => {
            throw new Error('409:VERSION_CONFLICT')
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const store = createGoalStore(failing, {
      baseUrl: () => '',
      token: () => 'token'
    })
    await store.refresh()
    expect(await store.setStatus(goal.id, 'archived')).toBe(false)
    expect(store.error.value).toBe('VERSION_CONFLICT')
  })
})

describe('goal AI breakdown', () => {
  it('previews suggestions and adds them as milestones', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    expect(await store.previewBreakdown(goal!.id, '调研, 原型, 上线')).toBe(true)
    const items = store.suggestions.value[goal!.id] ?? []
    expect(items.map((item) => item.title)).toEqual(['调研', '原型', '上线'])

    expect(await store.addSuggestion(goal!.id, items[0]!)).toBe(true)
    expect(store.goals.value[0]?.milestones).toHaveLength(1)
    expect(store.suggestions.value[goal!.id]).toHaveLength(2)

    expect(await store.addAllSuggestions(goal!.id)).toBe(2)
    expect(store.goals.value[0]?.milestones).toHaveLength(3)
    expect(store.goals.value[0]?.progress.totalMilestones).toBe(3)
    expect(store.suggestions.value[goal!.id]).toEqual([])
  })

  it('maps AI unavailable errors and requires a token', async () => {
    const bridge = new MemoryBridge()
    const goal = await bridge.goalCreate('', 'token', { title: 'Ship' })
    const failing: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'goalBreakdownPreview') {
          return async () => {
            throw new Error('503:AI_UNAVAILABLE')
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const store = createGoalStore(failing, { baseUrl: () => '', token: () => 'token' })
    await store.refresh()
    expect(await store.previewBreakdown(goal.id)).toBe(false)
    expect(store.error.value).toBe('AI_UNAVAILABLE')

    const signedOut = createStore('')
    expect(await signedOut.store.previewBreakdown('goal-1')).toBe(false)
    expect(signedOut.store.error.value).toBe('GOAL_SIGNED_OUT')
  })
})

describe('goal editing and milestone removal', () => {
  it('updates goal details with the current version', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship', '2026-12-31')
    expect(await store.updateDetails(goal!.id, {
      title: 'Ship v2',
      description: 'release plan',
      targetDate: '2027-01-31'
    })).toBe(true)
    expect(store.goals.value[0]?.title).toBe('Ship v2')
    expect(store.goals.value[0]?.description).toBe('release plan')
    expect(store.goals.value[0]?.targetDate).toBe('2027-01-31')
    expect(store.goals.value[0]?.version).toBe(2)

    expect(await store.updateDetails(goal!.id, { title: '   ' })).toBe(false)
    expect(store.error.value).toBe('INVALID_GOAL')
  })

  it('removes a milestone and recomputes progress', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    await store.addMilestone(goal!.id, 'Draft')
    await store.addMilestone(goal!.id, 'Review')
    let current = store.goals.value[0]!
    expect(await store.toggleMilestone(current.id, current.milestones[0]!)).toBe(true)
    current = store.goals.value[0]!
    expect(current.progress.percent).toBe(50)

    expect(await store.removeMilestone(current.id, current.milestones[1]!)).toBe(true)
    current = store.goals.value[0]!
    expect(current.milestones).toHaveLength(1)
    expect(current.progress).toEqual({
      completedMilestones: 1,
      totalMilestones: 1,
      percent: 100
    })
  })
})

describe('milestone ordering', () => {
  it('moves a milestone up and down by swapping positions', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    await store.addMilestone(goal!.id, 'Draft')
    await store.addMilestone(goal!.id, 'Review')
    await store.addMilestone(goal!.id, 'Ship')
    let current = store.goals.value[0]!
    expect(current.milestones.map((item) => item.title)).toEqual(['Draft', 'Review', 'Ship'])

    const review = current.milestones[1]!
    expect(await store.moveMilestone(current.id, review, 'up')).toBe(true)
    current = store.goals.value[0]!
    expect(current.milestones.map((item) => item.title)).toEqual(['Review', 'Draft', 'Ship'])

    const moved = current.milestones[0]!
    expect(await store.moveMilestone(current.id, moved, 'up')).toBe(false)
    expect(await store.moveMilestone(current.id, current.milestones[2]!, 'down')).toBe(false)
  })
})

describe('goal suggestions and archive filter', () => {
  it('drops suggestions that duplicate existing milestones or each other', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    await store.addMilestone(goal!.id, '调研')
    expect(await store.previewBreakdown(goal!.id, '调研, 原型, 原型, 上线')).toBe(true)
    expect((store.suggestions.value[goal!.id] ?? []).map((item) => item.title)).toEqual([
      '原型',
      '上线'
    ])
  })

  it('filters archived goals unless requested', () => {
    const base = {
      id: 'g',
      title: 'g',
      description: '',
      targetDate: null,
      version: 1,
      createdAt: '',
      updatedAt: '',
      progress: { completedMilestones: 0, totalMilestones: 0, percent: 0 },
      milestones: []
    }
    const goals = [
      { ...base, id: 'a', status: 'active' as const },
      { ...base, id: 'b', status: 'archived' as const },
      { ...base, id: 'c', status: 'completed' as const }
    ]
    expect(filterGoals(goals, false).map((goal) => goal.id)).toEqual(['a', 'c'])
    expect(filterGoals(goals, true)).toHaveLength(3)
  })
})

describe('goal deletion', () => {
  it('deletes a goal by version and clears its suggestions', async () => {
    const { store } = createStore()
    const goal = await store.create('Ship')
    await store.previewBreakdown(goal!.id, '调研, 上线')
    expect(await store.deleteGoal(goal!)).toBe(true)
    expect(store.goals.value).toHaveLength(0)
    expect(store.suggestions.value[goal!.id]).toBeUndefined()
  })

  it('maps delete conflicts', async () => {
    const bridge = new MemoryBridge()
    const goal = await bridge.goalCreate('', 'token', { title: 'Ship' })
    await bridge.goalUpdate('', 'token', goal.id, goal.version, { title: 'Renamed' })
    const failing: DesktopBridge = new Proxy(bridge, {
      get(target, property, receiver) {
        if (property === 'goalDelete') {
          return async () => {
            throw new Error('409:VERSION_CONFLICT')
          }
        }
        return Reflect.get(target, property, receiver)
      }
    })
    const store = createGoalStore(failing, { baseUrl: () => '', token: () => 'token' })
    await store.refresh()
    expect(await store.deleteGoal(store.goals.value[0]!)).toBe(false)
    expect(store.error.value).toBe('VERSION_CONFLICT')
  })
})
