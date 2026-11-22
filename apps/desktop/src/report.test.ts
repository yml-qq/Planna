import { describe, expect, it } from 'vitest'
import type { FocusStats } from './api'
import { focusStatsCsv, focusStatsSummary } from './report'

function stats(): FocusStats {
  return {
    from: '2026-11-16',
    to: '2026-11-22',
    timeZone: 'Asia/Shanghai',
    totalSessions: 3,
    completedSessions: 2,
    abandonedSessions: 1,
    actualSeconds: 3720,
    days: [
      { date: '2026-11-20', completedSessions: 1, actualSeconds: 1800 },
      { date: '2026-11-22', completedSessions: 1, actualSeconds: 1920 }
    ]
  }
}

describe('focus report', () => {
  it('formats a CSV with header and minute rows', () => {
    const csv = focusStatsCsv(stats())
    expect(csv.split('\r\n')).toEqual([
      'date,minutes,completed_sessions,actual_seconds',
      '2026-11-20,30,1,1800',
      '2026-11-22,32,1,1920',
      ''
    ])
  })

  it('formats a human summary', () => {
    expect(focusStatsSummary(stats())).toBe(
      '2026-11-16 ~ 2026-11-22（Asia/Shanghai）：62 分钟，完成 2 次，放弃 1 次'
    )
  })

  it('keeps an empty export valid', () => {
    const empty = { ...stats(), days: [], actualSeconds: 0, completedSessions: 0, abandonedSessions: 0 }
    expect(focusStatsCsv(empty)).toBe('date,minutes,completed_sessions,actual_seconds\r\n')
    expect(focusStatsSummary(empty)).toContain('0 分钟')
  })
})
