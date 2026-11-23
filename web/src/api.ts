// Same-origin JSON client. Every state-changing call carries X-TG-CSRF, which the server requires.

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message)
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: {
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...(method !== 'GET' ? { 'X-TG-CSRF': '1' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) {
    let code = `HTTP_${res.status}`
    let message = res.statusText
    try {
      const e = await res.json()
      code = e.error ?? code
      message = e.message ?? message
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, code, message)
  }
  if (res.status === 204) return undefined as T
  const type = res.headers.get('content-type') ?? ''
  return (type.includes('json') ? await res.json() : undefined) as T
}

/** For endpoints answering with a picture: returns an object URL to put in <img src>. */
async function image(method: string, url: string, body?: unknown): Promise<string> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: {
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...(method !== 'GET' ? { 'X-TG-CSRF': '1' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) {
    let code = `HTTP_${res.status}`
    let message = res.statusText
    try {
      const e = await res.json()
      code = e.error ?? code
      message = e.message ?? message
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, code, message)
  }
  return URL.createObjectURL(await res.blob())
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body: unknown = {}) => request<T>('POST', url, body),
  put: <T>(url: string, body: unknown = {}) => request<T>('PUT', url, body),
  image,
}

// ------------------------------------------------------------------ server types

export type Role = 'QUARRY_OPERATOR' | 'GATEKEEPER' | 'SUPERVISOR' | 'ACCOUNTANT' | 'MANAGER' | 'ADMIN' | 'AUDITOR'
export interface User { id: number; username: string; fullName: string; role: Role; language: 'ar' | 'fr' }
export interface Gate { id: number; code: string; name: string; siteId: number | null }
export interface TicketSummary {
  ticketId: string; deviceId: number; seq: number; truckId: number; driverId: number; operatorId: number
  issuedAt: number; ageSeconds: number; flags: number; shortCode: string
}
export interface ScanResult {
  scanId: number; tripId: number | null; tripNumber: string | null; outcome: 'ACCEPT' | 'PENDING' | 'REJECT'
  reasons: string[]; notes: string[]; ticket: TicketSummary | null
}
export interface TicketNames {
  fleetNumber: string | null; plate: string | null; driverName: string | null; driverIsDefault: boolean
  operatorName: string | null
}
export interface Snapshot { id: number; cameraId: number; cameraLabel: string; role: string; frameIndex: number; capturedAt: string }
export interface ScanView { kind: 'ENTRY'; scan: ScanResult; names: TicketNames | null; snapshots: Snapshot[] }
export interface TripCard {
  tripId: number; tripNumber: string; state: string; truckId: number; fleetNumber: string | null; plate: string | null
  driverName: string | null; ticketId: string; enteredAt: string; loadStatus: string | null; entryDecision: string | null
}
export interface ExitResult { scanId: number; outcome: 'ACCEPT' | 'PENDING' | 'REJECT'; reasons: string[]; notes: string[]; trip: TripCard | null }
export interface ExitView { kind: 'EXIT'; exit: ExitResult; snapshots: Snapshot[] }
export interface ExitDecisionResult { tripId: number; tripNumber: string; state: string; reviewId: number | null; reasons: string[] }
export interface ReceiptData {
  tripId: number; tripNumber: string; qrText: string; fleetNumber: string | null; plate: string | null; driverName: string | null
  gateName: string; enteredAt: string; loadStatus: string | null; printCount: number
}
export interface ReviewQueueItem {
  tripId: number; tripNumber: string; state: string; fleetNumber: string | null; plate: string | null; driverName: string | null
  enteredAt: string; exited: boolean; priority: 'HIGH' | 'NORMAL' | 'LOW'; isSample: boolean; reasons: string[]; openedAt: string
}
export interface ScanDetail {
  scanId: number; direction: 'ENTRY' | 'EXIT'; scanType: string; scannedAt: string; outcome: string; reasons: string[]; notes: string[]
  gatekeeperName: string | null; decision: string | null; decisionNote: string | null; gateName: string | null
}
export interface SnapshotDetail {
  id: number; scanId: number; cameraId: number; cameraLabel: string; role: string; frameIndex: number; capturedAt: string
  loadLabel: string | null; plateLabel: string | null; qualityLabel: string | null
}
export interface ReviewRecord {
  id: number; reasons: string[]; priority: string; isSample: boolean; createdAt: string; decision: string | null
  decisionReason: string | null; decidedByName: string | null; decidedAt: string | null
}
export interface StateEvent { fromState: string | null; toState: string; reasonCode: string; actor: string; at: string; note: string | null }
export interface TicketDetail {
  ticketId: string; issuedAt: string; clockDelta: number; flags: number; lat: number | null; lon: number | null; configVersion: number
  operatorName: string | null; firstSeenVia: string; hasPhoto: boolean; driverChangeReason: string | null; voided: boolean
}
export interface TripDetail {
  trip: TripCard; ticket: TicketDetail | null; scans: ScanDetail[]; snapshots: SnapshotDetail[]; reviews: ReviewRecord[]
  events: StateEvent[]; receiptPrints: number
}
export interface AlertItem {
  id: number; type: string; severity: 'CRITICAL' | 'MEDIUM' | 'INFO'; entity: string | null; entityId: string | null; params: string
  createdAt: string; smsSentAt: string | null; telegramSentAt: string | null; whatsappSentAt: string | null
  ackByName: string | null; ackAt: string | null
  failedDeliveries: number
}
export interface ChannelStatus {
  smsEnabled: boolean; smsTransport: string; smsPort: string; smsRouterUrl: string; smsRouterPasswordSet: boolean
  smsRecipients: string[]; smsMinSeverity: string; telegramEnabled: boolean
  telegramTokenSet: boolean; telegramChatIds: string[]; telegramMinSeverity: string
  whatsappEnabled: boolean; whatsappTokenSet: boolean; whatsappPhoneNumberId: string; whatsappRecipients: string[]
  whatsappTemplate: string; whatsappMinSeverity: string; language: string
}
export interface SettingItem { key: string; value: string; type: string; min: string | null; max: string | null }
export interface CountItem { key: string; label: string | null; count: number }
export interface RecentScan {
  scanId: number; scannedAt: string; direction: string; outcome: string; reasons: string[]; ticketId: string | null
  fleetNumber: string | null; gateName: string | null
}
export interface Dashboard {
  day: string; entries: number; exits: number; accepted: number; pending: number; rejected: number; partial: number; inside: number
  openReviews: number; openAlertsCritical: number; openAlertsOther: number; phonesOverdue: number
  byTruck: CountItem[]; byDriver: CountItem[]; byHour: CountItem[]; recent: RecentScan[]
}
export interface StatementLine { driverId: number; driverName: string; employmentType: string; trips: number; full: number; partial: number; amount: number }
export interface StatementTrip {
  tripId: number; tripNumber: string; enteredAt: string; driverId: number; driverName: string; truckId: number
  fleetNumber: string | null; loadStatus: string | null; state: string; amount: number
}
export interface Statement {
  from: string; to: string; pricePerTrip: number; partialPercent: number; currency: string; lines: StatementLine[]
  trips: StatementTrip[]; total: number; notYetPayable: number
}
export interface PaymentBatch {
  id: number; periodFrom: string; periodTo: string; pricePerTrip: number; currency: string; tripCount: number; total: number
  note: string | null; createdByName: string | null; createdAt: string
}
export interface DecisionResult {
  tripId: number; tripNumber: string; state: string; loadStatus: string | null; reviewId: number | null; reviewReasons: string[]
}
export interface Camera {
  id: number; label: string; gateId: number | null; role: 'PLATE' | 'CARGO' | 'OVERVIEW'; captureOnScan: boolean
  frames: number; frameIntervalMs: number; source: 'ONVIF' | 'URL'; onvifUrl: string | null; profileToken: string | null
  snapshotUrl: string; username: string | null; hasPassword: boolean; manufacturer: string | null; model: string | null
  configVersion: number; active: boolean; lastOkAt: string | null; lastError: string | null; lastErrorAt: string | null
}
export interface Discovered { endpointId: string; xAddrs: string[]; name: string | null; hardware: string | null; fromIp: string }
export interface OnvifProfile { token: string; name: string; width: number | null; height: number | null; snapshotUri: string | null }
export interface OnvifInfo { manufacturer: string | null; model: string | null; firmware: string | null; serial: string | null; profiles: OnvifProfile[] }
export interface Device {
  id: number; kind: string; label: string; enrolledAt: string; revokedAt: string | null; revokeReason: string | null; lastSyncAt: string | null
}
export interface Invite { qrText: string; expiresAt: string }
export interface ServerAddressCode { url: string; qrText: string }
export interface Truck { id: number; fleetNumber: string; model: string | null; plate: string | null; defaultDriverId: number | null; active: boolean }
export interface Driver { id: number; fullName: string; phone: string | null; employmentType: string; active: boolean }
export interface Site { id: number; code: string; name: string; kind: 'QUARRY' | 'FACTORY'; hasGeofence: boolean }
export interface UserItem {
  id: number; username: string; fullName: string; role: Role; language: string; active: boolean; hasSecret: boolean
  lastLoginAt: string | null; lockedUntil: string | null
}
export interface AuditCheck { ok: boolean; checked: number; firstBadId: number | null; problem: string | null }
export interface Anchor { auditId: number; hash: string; createdAt: string }
export interface Gap { deviceId: number; gapFrom: number; gapTo: number }
