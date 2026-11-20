import { describe, expect, it } from 'vitest'
import { MemoryBridge, type DesktopBridge } from './api'
import { createGoalStore } from './goals'

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
