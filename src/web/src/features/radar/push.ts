/** Web-push opt-in helpers. Degrade gracefully without SW/HTTPS/permission. */

export type PushEnableResult =
  | { ok: true; subscription: PushSubscriptionJSON }
  | { ok: false; reason: 'unsupported' | 'denied' | 'failed' }

export function pushSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    'Notification' in window &&
    'serviceWorker' in navigator &&
    'PushManager' in window
  )
}

/** Requests permission and subscribes with the given VAPID public key. */
export async function enablePush(vapidPublicKey: string): Promise<PushEnableResult> {
  if (!pushSupported()) return { ok: false, reason: 'unsupported' }
  try {
    const permission = await Notification.requestPermission()
    if (permission !== 'granted') return { ok: false, reason: 'denied' }

    const registration = await navigator.serviceWorker.ready
    const subscription =
      (await registration.pushManager.getSubscription()) ??
      (await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(vapidPublicKey),
      }))
    return { ok: true, subscription: subscription.toJSON() }
  } catch {
    return { ok: false, reason: 'failed' }
  }
}

export function urlBase64ToUint8Array(base64String: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4)
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/')
  const rawData = window.atob(base64)
  const output = new Uint8Array(new ArrayBuffer(rawData.length))
  for (let i = 0; i < rawData.length; i += 1) {
    output[i] = rawData.charCodeAt(i)
  }
  return output
}
