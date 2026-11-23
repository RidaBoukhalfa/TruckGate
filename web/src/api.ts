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
  operatorName: string | null; truckStatus: TruckStatus | null; hasPhoto: boolean; photoExpected: boolean; factoryName: string | null
}
export type TruckStatus = 'PENDING_APPROVAL' | 'APPROVED' | 'REJECTED' | 'MERGED'
export interface TruckPassage {
  tripId: number; tripNumber: string; ticketId: string; enteredAt: string; exitedAt: string | null; state: string; loadStatus: string | null
}
export type VideoStatus = 'RECORDING' | 'OK' | 'PARTIAL' | 'FAILED' | 'PURGED'
export interface VideoItem {
  id: number; scanId: number; cameraId: number; cameraLabel: string; direction: 'ENTRY' | 'EXIT'; status: VideoStatus
  scanAt: string; windowStart: string; windowEnd: string; durationMs: number | null; sizeBytes: number | null; error: string | null
}
export interface Snapshot { id: number; cameraId: number; cameraLabel: string; role: string; frameIndex: number; capturedAt: string }
export interface ScanView {
  kind: 'ENTRY'; scan: ScanResult; names: TicketNames | null; snapshots: Snapshot[]; passages: TruckPassage[] | null
  videos: VideoItem[] | null
}
export interface TripCard {
  tripId: number; tripNumber: string; state: string; truckId: number; fleetNumber: string | null; plate: string | null
  driverName: string | null; ticketId: string; enteredAt: string; loadStatus: string | null; entryDecision: string | null
}
export interface ExitResult { scanId: number; outcome: 'ACCEPT' | 'PENDING' | 'REJECT'; reasons: string[]; notes: string[]; trip: TripCard | null }
export interface ExitView { kind: 'EXIT'; exit: ExitResult; snapshots: Snapshot[]; videos: VideoItem[] | null }
export interface ExitDecisionResult { tripId: number; tripNumber: string; state: string; reviewId: number | null; reasons: string[] }
export interface ReceiptData {
  tripId: number; tripNumber: string; qrText: string; fleetNumber: string | null; plate: string | null; driverName: string | null
  gateName: string; enteredAt: string; loadStatus: string | null; printCount: number
}
export interface ReviewQueueItem {
  tripId: number; tripNumber: string; state: string; fleetNumber: string | null; plate: string | null; driverName: string | null
  enteredAt: string; exited: boolean; priority: 'HIGH' | 'NORMAL' | 'LOW'; isSample: boolean; reasons: string[]; openedAt: string
  investigating: boolean; lastNote: string | null; truckStatus: TruckStatus | null
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
export interface ReviewNote { id: number; kind: 'INVESTIGATE' | 'NOTE'; note: string; createdByName: string | null; createdAt: string }
export interface TripAnnotation { id: number; labels: string[]; note: string | null; annotatedByName: string | null; annotatedAt: string }
export interface TruckSummary {
  id: number; fleetNumber: string; approvalStatus: TruckStatus; ownerLastName: string | null; ownerFirstName: string | null
  reportedPlate: string | null; mergedInto: number | null
}
export interface TripDetail {
  trip: TripCard; ticket: TicketDetail | null; scans: ScanDetail[]; snapshots: SnapshotDetail[]; reviews: ReviewRecord[]
  events: StateEvent[]; receiptPrints: number; notes: ReviewNote[]; annotations: TripAnnotation[]; videos: VideoItem[]
  truck: TruckSummary | null
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
export interface SettingItem { key: string; value: string; type: string; min: string | null; max: string | null; adminOnly: boolean }
export interface CountItem { key: string; label: string | null; count: number }
export interface RecentScan {
  scanId: number; scannedAt: string; direction: string; outcome: string; reasons: string[]; ticketId: string | null
  fleetNumber: string | null; gateName: string | null
}
export interface Dashboard {
  day: string; entries: number; exits: number; accepted: number; pending: number; rejected: number; partial: number; inside: number
  openReviews: number; openAlertsCritical: number; openAlertsOther: number; phonesOverdue: number
  byTruck: CountItem[]; byDriver: CountItem[]; byHour: CountItem[]; recent: RecentScan[]
  tripsToday: number; completedToday: number; trucksAwaitingApproval: number; burnedToday: number; cameraFailures: number
  recordingFailuresToday: number; syncFailures: number; unclosedTrips: number
}
export interface StatementLine { driverId: number; driverName: string; employmentType: string; trips: number; full: number; partial: number; amount: number }
export interface StatementTrip {
  tripId: number; tripNumber: string; enteredAt: string; driverId: number; driverName: string; truckId: number
  fleetNumber: string | null; loadStatus: string | null; state: string; amount: number; pricePerTrip: number | null
  effectiveTruckId: number
}
export interface TruckStatementLine {
  truckId: number; fleetNumber: string | null; ownerLastName: string | null; ownerFirstName: string | null; plate: string | null
  trips: number; full: number; partial: number; pricePerTrip: number | null; tripsAmount: number; adjustments: number; total: number
  paidTrips: number; paidAmount: number; paymentStatus: 'UNPAID' | 'PAID' | 'NONE'
}
export interface ExcludedTrip {
  tripId: number; tripNumber: string; enteredAt: string; truckId: number; fleetNumber: string | null; state: string; reason: string
}
export interface Adjustment {
  id: number; truckId: number; fleetNumber: string | null; tripId: number | null; tripNumber: string | null; amount: number
  reason: string; createdByName: string | null; createdAt: string; batchId: number | null; appliedAt: string | null
}
export interface Statement {
  from: string; to: string; pricePerTrip: number | null; partialPercent: number; currency: string; lines: StatementLine[]
  trips: StatementTrip[]; total: number; notYetPayable: number; trucks: TruckStatementLine[]; excluded: ExcludedTrip[]
  adjustments: Adjustment[]; tripsTotal: number; adjustmentsTotal: number
}
export interface PaymentBatch {
  id: number; periodFrom: string; periodTo: string; pricePerTrip: number | null; currency: string; tripCount: number; total: number
  note: string | null; createdByName: string | null; createdAt: string; pricing: 'GLOBAL' | 'PER_TRUCK'; adjustmentsTotal: number
}
export interface DecisionResult {
  tripId: number; tripNumber: string; state: string; loadStatus: string | null; reviewId: number | null; reviewReasons: string[]
}
export interface Camera {
  id: number; label: string; gateId: number | null; role: 'PLATE' | 'CARGO' | 'OVERVIEW'; captureOnScan: boolean
  frames: number; frameIntervalMs: number; source: 'ONVIF' | 'URL'; onvifUrl: string | null; profileToken: string | null
  snapshotUrl: string; username: string | null; hasPassword: boolean; manufacturer: string | null; model: string | null
  configVersion: number; active: boolean; lastOkAt: string | null; lastError: string | null; lastErrorAt: string | null
  streamUrl: string | null; recordVideo: boolean; location: string | null; recorderStatus: 'OFF' | 'STARTING' | 'RECORDING' | 'ERROR' | null
  recorderError: string | null; lastFrameAt: string | null; recordingsOk24h: number; recordingsFailed24h: number
}
export interface StorageStatus {
  root: string; freeBytes: number; totalBytes: number; videoBytes: number; recordings: number; minFreeGb: number; low: boolean
}
export interface Discovered { endpointId: string; xAddrs: string[]; name: string | null; hardware: string | null; fromIp: string }
export interface OnvifProfile {
  token: string; name: string; width: number | null; height: number | null; snapshotUri: string | null; streamUri: string | null
}
export interface OnvifInfo { manufacturer: string | null; model: string | null; firmware: string | null; serial: string | null; profiles: OnvifProfile[] }
export interface Device {
  id: number; kind: string; label: string; enrolledAt: string; revokedAt: string | null; revokeReason: string | null; lastSyncAt: string | null
}
export interface Invite { qrText: string; expiresAt: string }
export interface ServerAddressCode { url: string; qrText: string }
export interface Truck { id: number; fleetNumber: string; model: string | null; plate: string | null; defaultDriverId: number | null; active: boolean }
export interface FleetTruck {
  id: number; fleetNumber: string; ownerLastName: string | null; ownerFirstName: string | null; plate: string | null; model: string | null
  defaultDriverId: number | null; defaultDriverName: string | null; active: boolean; approvalStatus: TruckStatus; createdVia: 'ADMIN' | 'PHONE'
  createdAt: string; updatedAt: string; price: number | null; reportedPlate: string | null; createdByDevice: number | null
  createdByDeviceLabel: string | null; createdByName: string | null; possibleDuplicateOf: number | null
  duplicateFleetNumber: string | null; duplicatePlate: string | null; decidedByName: string | null; decidedAt: string | null
  decisionReason: string | null; mergedInto: number | null; tripCount: number; lastTripAt: string | null; observedPlates: string[]
  complete: boolean
}
export interface PriceItem { id: number; price: number; validFrom: string; setByName: string | null; reason: string | null }
export interface PlateItem { plateText: string; validFrom: string; validTo: string | null }
export interface TruckTripItem {
  tripId: number; tripNumber: string; ticketId: string; enteredAt: string; exitedAt: string | null; state: string
  loadStatus: string | null; driverName: string | null; truckId: number
}
export interface TruckDriverItem { driverId: number; driverName: string; trips: number; isDefault: boolean }
export interface AuditItem { id: number; at: string; actor: string; action: string; entity: string; entityId: string | null; payload: string }
export interface TruckHistory {
  truck: FleetTruck; prices: PriceItem[]; plates: PlateItem[]; trips: TruckTripItem[]; drivers: TruckDriverItem[]; audit: AuditItem[]
}
export type Lifecycle = 'SCANNED_AT_ENTRY' | 'ENTRY_REVIEW' | 'INSIDE_FACTORY' | 'SCANNED_AT_EXIT' | 'CLOSED' | 'REJECTED'
export interface BurnedReceipt {
  tripId: number; tripNumber: string; ticketId: string; deviceId: number; seq: number; truckId: number; fleetNumber: string | null
  plate: string | null; truckStatus: TruckStatus | null; driverName: string | null; operatorName: string | null; issuedAt: string | null
  enteredAt: string; exitedAt: string | null; entryOutcome: string; exitOutcome: string | null; state: string; lifecycle: Lifecycle
  paid: boolean; voidedAfterUse: boolean; loadStatus: string | null; entryVideo: VideoStatus | null; exitVideo: VideoStatus | null
  reviewStatus: 'OPEN' | 'INVESTIGATING' | 'DECIDED' | 'NONE'; hasPhoto: boolean; photoExpected: boolean
}
export interface BurnedPage { total: number; items: BurnedReceipt[] }
export interface ActivityItem {
  at: string; kind: string; code: string; tripId: number | null; tripNumber: string | null; fleetNumber: string | null; detail: string | null
}
export interface Driver { id: number; fullName: string; phone: string | null; employmentType: string; active: boolean }
export interface Site { id: number; code: string; name: string; kind: 'QUARRY' | 'FACTORY'; hasGeofence: boolean }
export interface UserItem {
  id: number; username: string; fullName: string; role: Role; language: string; active: boolean; hasSecret: boolean
  lastLoginAt: string | null; lockedUntil: string | null
}
export interface AuditCheck { ok: boolean; checked: number; firstBadId: number | null; problem: string | null }
export interface Anchor { auditId: number; hash: string; createdAt: string }
export interface Gap { deviceId: number; gapFrom: number; gapTo: number }
