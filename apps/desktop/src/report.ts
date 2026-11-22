import type { FocusStats } from './api'

const HEADER = 'date,minutes,completed_sessions,actual_seconds'

export function focusStatsCsv(stats: FocusStats): string {
  const rows = stats.days.map((day) => {
    const minutes = Math.round(day.actualSeconds / 60)
    return `${day.date},${minutes},${day.completedSessions},${day.actualSeconds}`
  })
  return [HEADER, ...rows].join('\r\n') + '\r\n'
}

export function focusStatsSummary(stats: FocusStats): string {
  return (
    `${stats.from} ~ ${stats.to}（${stats.timeZone}）：` +
    `${Math.round(stats.actualSeconds / 60)} 分钟，完成 ${stats.completedSessions} 次，` +
    `放弃 ${stats.abandonedSessions} 次`
  )
}
