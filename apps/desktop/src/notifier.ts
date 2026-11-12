import type { Notifier } from './reminders'

export class MemoryNotifier implements Notifier {
  readonly sent: Array<{ title: string; body: string }> = []

  async notify(title: string, body: string): Promise<void> {
    this.sent.push({ title, body })
  }
}

/** Uses the Tauri notification plugin in the shell and stays inert in a plain browser. */
export async function createNotifier(): Promise<Notifier> {
  if (typeof window === 'undefined' || !('__TAURI_INTERNALS__' in window)) {
    return new MemoryNotifier()
  }
  const plugin = await import('@tauri-apps/plugin-notification')
  return {
    async notify(title: string, body: string): Promise<void> {
      let granted = await plugin.isPermissionGranted()
      if (!granted) {
        granted = (await plugin.requestPermission()) === 'granted'
      }
      if (granted) {
        plugin.sendNotification({ title, body })
      }
    }
  }
}
