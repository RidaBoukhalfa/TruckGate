import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import {
  api, type DecisionResult, type ExitDecisionResult, type ExitView, type Gate, type ReceiptData, type ScanView, type Snapshot,
  type TripCard, type VideoItem,
} from '../api'
import { useI18n, type Key } from '../i18n'
import { printReceipt } from '../print'
import { ErrorBox, fmtTime, useAction } from '../ui'

const entryDecisions = ['FULL', 'PARTIAL', 'EMPTY', 'DIFFERENT_TRUCK', 'PLATE_UNREADABLE'] as const
const exitDecisions = ['EMPTY', 'LOADED'] as const

type View = ScanView | ExitView
export type GateStatus = 'VALID' | 'PENDING_REVIEW' | 'PENDING_TRUCK' | 'REJECTED' | 'ALREADY_USED' | 'UNKNOWN_RECEIPT'

// Reasons meaning "this is not a ticket / receipt issued by our phones or this gate".
const notOurs = ['MALFORMED', 'UNKNOWN_DEVICE', 'BAD_SIGNATURE', 'WRONG_DEVICE_KIND', 'WRONG_KIND', 'QR_INVALID', 'FORGED_RECEIPT']
const statusClass: Record<GateStatus, string> = {
  VALID: 's-valid', PENDING_REVIEW: 's-review', PENDING_TRUCK: 's-truck', REJECTED: 's-rejected', ALREADY_USED: 's-used',
  UNKNOWN_RECEIPT: 's-unknown',
}

/** The six screen states the gatekeeper is trained on, derived from the server's outcome and reasons. */
export function gateStatus(outcome: string, reasons: string[]): GateStatus {
  if (outcome === 'REJECT') {
    if (reasons.includes('ALREADY_USED') || reasons.includes('ALREADY_EXITED')) return 'ALREADY_USED'
    if (reasons.some((r) => notOurs.includes(r))) return 'UNKNOWN_RECEIPT'
    return 'REJECTED'
  }
  if (outcome === 'PENDING') {
    return reasons.includes('TRUCK_PENDING_APPROVAL') || reasons.includes('UNKNOWN_TRUCK') ? 'PENDING_TRUCK' : 'PENDING_REVIEW'
  }
  return 'VALID'
}

/** Live recording state of the passage clips (polled while a clip is still being recorded). */
function RecordingStrip({ scanId, initial }: { scanId: number; initial: VideoItem[] | null }) {
  const { t, reason } = useI18n()
  const [videos, setVideos] = useState<VideoItem[] | null>(initial)
  useEffect(() => {
    setVideos(initial)
    if (!initial?.some((v) => v.status === 'RECORDING')) return
    const started = Date.now()
    const timer = setInterval(async () => {
      try {
        const v = await api.get<VideoItem[]>(`/api/gate/scans/${scanId}/videos`)
        setVideos(v)
        if (!v.some((x) => x.status === 'RECORDING') || Date.now() - started > 180_000) clearInterval(timer)
      } catch {
        clearInterval(timer)
      }
    }, 3000)
    return () => clearInterval(timer)
  }, [scanId, initial])

  if (videos === null) return null
  if (videos.length === 0) return <div className="muted small">{t('noRecordingCams')}</div>
  return (
    <div className="rec-strip">
      {videos.map((v) => {
        const cls = v.status === 'RECORDING' ? 'recording' : v.status === 'OK' ? 'ok' : v.status === 'FAILED' ? 'failed' : ''
        return (
          <span key={v.id} className={`rec ${cls}`} title={v.error ? reason(v.error) : undefined}>
            <span className="dot" />{v.cameraLabel} · {t(`v${v.status}` as Key)}
          </span>
        )
      })}
    </div>
  )
}

/**
 * Gatekeeper station. The scanner (USB, keyboard mode, Enter at the end) types into the always-focused
 * field. A quarry ticket (TG1:) is an entry, the gate receipt (TR1:) is an exit; the server checks it,
 * the selected cameras take their pictures and the video recorders keep the clip around the scan.
 * Keys 1–5 decide an entry, 1–2 an exit; Esc clears.
 */
export default function GatePage() {
  const i18n = useI18n()
  const { t, reason, lang } = i18n
  const [gates, setGates] = useState<Gate[]>([])
  const [gateId, setGateId] = useState<number | null>(() => Number(localStorage.getItem('tg.gate')) || null)
  const [autoPrint, setAutoPrint] = useState(() => localStorage.getItem('tg.autoPrint') !== '0')
  const [input, setInput] = useState('')
  const [view, setView] = useState<View | null>(null)
  const [entryDone, setEntryDone] = useState<DecisionResult | null>(null)
  const [exitDone, setExitDone] = useState<ExitDecisionResult | null>(null)
  const [zoom, setZoom] = useState<Snapshot | 'photo' | null>(null)
  const [inside, setInside] = useState<TripCard[]>([])
  const [manual, setManual] = useState({ open: false, ticketId: '', code: '' })
  const [plate, setPlate] = useState('')
  const { busy, error, setError, run } = useAction()
  const field = useRef<HTMLInputElement>(null)

  const loadInside = useCallback(() => {
    api.get<TripCard[]>('/api/gate/inside').then(setInside).catch(() => undefined)
  }, [])

  useEffect(() => {
    api.get<Gate[]>('/api/gate/gates').then((g) => {
      setGates(g)
      if (g.length === 1) setGateId(g[0].id)
    }).catch(() => setGates([]))
    loadInside()
    const timer = setInterval(loadInside, 30_000)
    return () => clearInterval(timer)
  }, [loadInside])
  useEffect(() => { if (gateId) localStorage.setItem('tg.gate', String(gateId)) }, [gateId])
  useEffect(() => { localStorage.setItem('tg.autoPrint', autoPrint ? '1' : '0') }, [autoPrint])

  // Keep the scanner field focused, except while the gatekeeper types in another field (plate, manual code).
  const focus = useCallback(() => setTimeout(() => {
    if (manual.open) return
    const a = document.activeElement
    if (a && a !== field.current && ['INPUT', 'TEXTAREA', 'SELECT'].includes(a.tagName)) return
    field.current?.focus()
  }, 0), [manual.open])
  const clear = useCallback(() => {
    setView(null); setEntryDone(null); setExitDone(null); setZoom(null); setError(null); setInput(''); setPlate('')
    setTimeout(() => field.current?.focus(), 0)
  }, [setError])

  const show = (v: View | undefined) => {
    setInput('')
    if (v) setView(v)
    focus()
  }
  const reset = () => { setView(null); setEntryDone(null); setExitDone(null); setZoom(null); setPlate('') }

  const scan = async (text: string) => {
    if (!gateId || !text.trim()) return
    reset()
    show(await run(() => api.post<View>('/api/gate/scan', { gateId, qrText: text.trim() })))
  }

  const manualEntry = async (e: FormEvent) => {
    e.preventDefault()
    if (!gateId) return
    reset()
    const v = await run(() => api.post<ScanView>('/api/gate/manual', { gateId, ticketId: manual.ticketId, shortCode: manual.code }))
    if (v) setManual({ open: false, ticketId: '', code: '' })
    show(v)
  }

  const manualExit = async (c: TripCard) => {
    if (!gateId || !window.confirm(t('confirmExitNoReceipt', { truck: c.fleetNumber ?? c.tripNumber }))) return
    reset()
    show(await run(() => api.post<ExitView>('/api/gate/exit/manual', { gateId, tripId: c.tripId })))
  }

  const print = useCallback(async (tripId: number) => {
    const r = await run(() => api.post<ReceiptData>(`/api/gate/trips/${tripId}/receipt`))
    if (r) await printReceipt(r, i18n)
  }, [run, i18n])

  const entryReasons = view?.kind === 'ENTRY' ? view.scan.reasons : []
  const newTruck = entryReasons.includes('TRUCK_PENDING_APPROVAL') || entryReasons.includes('UNKNOWN_TRUCK')
  const plateKnown = view?.kind === 'ENTRY' && !!view.names?.plate

  const decideEntry = useCallback(async (d: (typeof entryDecisions)[number]) => {
    if (view?.kind !== 'ENTRY' || !view.scan.tripId || entryDone) return
    // A new truck the server has no plate for yet: the gatekeeper writes what he reads before letting it in.
    if (newTruck && !plateKnown && (d === 'FULL' || d === 'PARTIAL') && !plate.trim()) {
      setError(t('observedPlateHelp'))
      return
    }
    const r = await run(() => api.post<DecisionResult>(`/api/gate/scans/${view.scan.scanId}/decision`,
      { decision: d, observedPlate: newTruck && plate.trim() ? plate.trim() : null }))
    if (r) {
      setEntryDone(r)
      loadInside()
      if (autoPrint) await print(r.tripId)
      setTimeout(clear, 6000)
    }
    focus()
  }, [view, entryDone, run, clear, focus, autoPrint, print, loadInside, newTruck, plateKnown, plate, setError, t])

  const decideExit = useCallback(async (d: (typeof exitDecisions)[number]) => {
    if (view?.kind !== 'EXIT' || !view.exit.trip || view.exit.outcome === 'REJECT' || exitDone) return
    const r = await run(() => api.post<ExitDecisionResult>(`/api/gate/exit/scans/${view.exit.scanId}/decision`, { decision: d }))
    if (r) {
      setExitDone(r)
      loadInside()
      setTimeout(clear, 5000)
    }
    focus()
  }, [view, exitDone, run, clear, focus, loadInside])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (manual.open) return
      if (e.key === 'Escape') { clear(); return }
      const target = e.target as HTMLElement | null
      if (target && target !== field.current && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return
      if (input !== '' || !view) return
      if (view.kind === 'ENTRY' && view.scan.tripId && !entryDone && e.key >= '1' && e.key <= '5') {
        e.preventDefault()
        decideEntry(entryDecisions[Number(e.key) - 1])
      } else if (view.kind === 'EXIT' && !exitDone && (e.key === '1' || e.key === '2')) {
        e.preventDefault()
        decideExit(exitDecisions[Number(e.key) - 1])
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [view, entryDone, exitDone, input, decideEntry, decideExit, clear, manual.open])

  if (!gateId) {
    return (
      <div className="card">
        <h2>{t('chooseGate')}</h2>
        {gates.length === 0 && <div className="loading">{t('loading')}</div>}
        <div className="row">{gates.map((g) => <button key={g.id} className="primary" onClick={() => setGateId(g.id)}>{g.name}</button>)}</div>
      </div>
    )
  }

  const snapshots = view?.snapshots ?? []
  const firstPerCamera = snapshots.filter((s) => s.frameIndex === 0)
  const outcome = view?.kind === 'ENTRY' ? view.scan.outcome : view?.kind === 'EXIT' ? view.exit.outcome : null
  const reasons = view?.kind === 'ENTRY' ? view.scan.reasons : view?.kind === 'EXIT' ? view.exit.reasons : []
  const notes = view?.kind === 'ENTRY' ? view.scan.notes : view?.kind === 'EXIT' ? view.exit.notes : []
  const status = outcome ? gateStatus(outcome, reasons) : null
  const ticket = view?.kind === 'ENTRY' ? view.scan.ticket : null
  const names = view?.kind === 'ENTRY' ? view.names : null
  const photoUrl = ticket && names?.hasPhoto ? `/api/tickets/${ticket.deviceId}/${ticket.seq}/photo` : null
  const scanId = view?.kind === 'ENTRY' ? view.scan.scanId : view?.kind === 'EXIT' ? view.exit.scanId : null

  return (
    <div className="gate-layout">
      <div className="gate" onClick={() => { if (!zoom && !manual.open) focus() }}>
        <div className="scanbar">
          <input
            ref={field}
            autoFocus
            className="scaninput"
            placeholder={busy ? t('scanning') : t('scanHere')}
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); scan(input) } }}
            onBlur={() => { if (!zoom && !manual.open) focus() }}
            disabled={busy}
            aria-label={t('scanHere')}
          />
          <span className="gate-name">{gates.find((g) => g.id === gateId)?.name}</span>
        </div>
        <div className="row small no-print">
          <label className="inline"><input type="checkbox" checked={autoPrint} onChange={(e) => setAutoPrint(e.target.checked)} /> {t('autoPrint')}</label>
          <button className="link" onClick={(e) => { e.stopPropagation(); setManual({ ...manual, open: !manual.open }) }}>{t('manualCode')}</button>
        </div>
        {manual.open && (
          <form className="row card" onSubmit={manualEntry} onClick={(e) => e.stopPropagation()}>
            <label>{t('ticketNumber')}<input autoFocus value={manual.ticketId} onChange={(e) => setManual({ ...manual, ticketId: e.target.value })} /></label>
            <label>{t('shortCode')}<input value={manual.code} className="narrow-wide" onChange={(e) => setManual({ ...manual, code: e.target.value })} /></label>
            <button className="primary" disabled={busy || !manual.ticketId || !manual.code}>{t('check')}</button>
            <button type="button" onClick={() => setManual({ open: false, ticketId: '', code: '' })}>{t('close')}</button>
          </form>
        )}
        <ErrorBox error={error} />

        {view && status && outcome && (
          <div className={`status ${statusClass[status]}`} role="status">
            <div className="status-word">{t(`st${status}` as Key)}</div>
            <div className="status-title">
              {view.kind === 'EXIT' && status !== 'ALREADY_USED' && status !== 'UNKNOWN_RECEIPT'
                ? t(`outcomeExit${outcome}` as Key) : t(`stTitle${status}` as Key)}
            </div>
            <div className="status-lines">
              {reasons.filter((r) => r !== 'TRUCK_PENDING_APPROVAL').map((r) => reason(r)).join(' · ')}
            </div>
          </div>
        )}

        {view && (
          <div className="gate-grid">
            <div className="card ticketinfo">
              {view.kind === 'ENTRY' ? (ticket ? (
                <>
                  <div className="row-between">
                    <div className="big">{names?.fleetNumber ?? ticket.truckId}</div>
                    {names?.truckStatus && names.truckStatus !== 'APPROVED' && (
                      <span className="badge amber">{t(`ts${names.truckStatus}` as Key)}</span>
                    )}
                    {!names?.truckStatus && newTruck && <span className="badge amber">{t('tsUNKNOWN')}</span>}
                  </div>
                  <div className="plate">{names?.plate ?? '— — —'}</div>
                  <dl>
                    <dt>{t('ticket')}</dt><dd><span className="code">{ticket.ticketId}</span> · <span className="code">{ticket.shortCode}</span></dd>
                    <dt>{t('driver')}</dt>
                    <dd><bdi>{names?.driverName ?? ticket.driverId}</bdi>{' '}
                      <span className={names?.driverIsDefault ? 'ok small' : 'warn small'}>
                        ({t(names?.driverIsDefault ? 'usualDriver' : 'otherDriver')})
                      </span>
                    </dd>
                    <dt>{t('operator')}</dt><dd><bdi>{names?.operatorName ?? ticket.operatorId}</bdi></dd>
                    <dt>{t('receiptCreated')}</dt>
                    <dd>{new Date(ticket.issuedAt * 1000).toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { dateStyle: 'short', timeStyle: 'short' })}
                      {' · '}{t('minutesAgo', { n: Math.round(ticket.ageSeconds / 60) })}</dd>
                  </dl>
                  {notes.length > 0 && (
                    <ul className="warnings">{notes.map((r) => <li key={r}>{reason(r)}</li>)}</ul>
                  )}
                  <div className="passages">
                    <strong>{t('previousOps')}</strong>
                    {(view.passages ?? []).length === 0 && <div className="muted">{t('noPrevious')}</div>}
                    {(view.passages ?? []).map((p) => (
                      <div key={p.tripId}>
                        <span className="code">{p.tripNumber}</span>
                        <span className="muted">{fmtTime(p.enteredAt, lang)}{p.exitedAt ? ` → ${fmtTime(p.exitedAt, lang)}` : ''}</span>
                        <span>{p.loadStatus ? t(`d${p.loadStatus}` as Key) : t(`state${p.state}` as Key)}</span>
                      </div>
                    ))}
                  </div>
                </>
              ) : <div className="muted">{t('none')}</div>) : view.exit.trip ? (
                <>
                  <div className="big">{view.exit.trip.fleetNumber ?? view.exit.trip.truckId}</div>
                  <div className="plate">{view.exit.trip.plate ?? '— — —'}</div>
                  <dl>
                    <dt>{t('trip')}</dt><dd><span className="code">{view.exit.trip.tripNumber}</span> · <span className="code">{view.exit.trip.ticketId}</span></dd>
                    <dt>{t('driver')}</dt><dd>{view.exit.trip.driverName ?? '—'}</dd>
                    <dt>{t('entered')}</dt>
                    <dd>{fmtTime(view.exit.trip.enteredAt, lang)} · {t('minutesAgo', { n: Math.round((Date.now() - Date.parse(view.exit.trip.enteredAt)) / 60000) })}</dd>
                    <dt>{t('entryDecision')}</dt><dd>{view.exit.trip.entryDecision ? t(`d${view.exit.trip.entryDecision}` as Key) : '—'}</dd>
                  </dl>
                  {notes.length > 0 && <ul className="warnings">{notes.map((r) => <li key={r}>{reason(r)}</li>)}</ul>}
                </>
              ) : <div className="muted">{t('none')}</div>}
            </div>

            <div className="card evidence">
              {view.kind === 'ENTRY' && ticket && (
                <div>
                  <h3>{t('cargoPhoto')}</h3>
                  {photoUrl
                    ? <img className="cargo-photo" src={photoUrl} alt={t('cargoPhoto')} onClick={(e) => { e.stopPropagation(); setZoom('photo') }} />
                    : <div className={names?.photoExpected === false ? 'muted small' : 'warn small'}>
                      {names?.photoExpected === false ? t('photoNone') : t('photoNotSynced')}
                    </div>}
                </div>
              )}
              {scanId !== null && (
                <div>
                  <h3>{t('recording')}</h3>
                  <RecordingStrip scanId={scanId} initial={view.videos} />
                </div>
              )}
              <div>
                <h3>{t('pictures')}</h3>
                {firstPerCamera.length === 0 && <div className="warn small">{t('noPictures')}</div>}
                <div className="pictures">
                  {firstPerCamera.map((s) => (
                    <figure key={s.id} onClick={(e) => { e.stopPropagation(); setZoom(s) }}>
                      <img src={`/api/snapshots/${s.id}`} alt={s.cameraLabel} />
                      <figcaption>{s.cameraLabel} · {t(`role${s.role}` as Key)}</figcaption>
                    </figure>
                  ))}
                </div>
              </div>
            </div>
          </div>
        )}

        {view?.kind === 'ENTRY' && view.scan.tripId && !entryDone && (
          <div className="decisions" onClick={(e) => e.stopPropagation()}>
            {newTruck && (
              <label className="observed">
                {t('observedPlate')}{!plateKnown && ' *'}
                <input className="plate-input" value={plate} inputMode="numeric" placeholder="01234 116 16"
                  onChange={(e) => setPlate(e.target.value)} />
              </label>
            )}
            <span>{t('decide')}:</span>
            {entryDecisions.map((d, i) => (
              <button key={d} className={d === 'FULL' || d === 'PARTIAL' ? 'primary' : 'danger'} disabled={busy}
                onClick={() => decideEntry(d)}>
                <kbd>{i + 1}</kbd> {t(`d${d}` as Key)}
              </button>
            ))}
          </div>
        )}

        {view?.kind === 'EXIT' && view.exit.trip && view.exit.outcome !== 'REJECT' && !exitDone && (
          <div className="decisions">
            <span>{t('exitDecision')}:</span>
            {exitDecisions.map((d, i) => (
              <button key={d} className={d === 'EMPTY' ? 'primary' : 'danger'} disabled={busy}
                onClick={(e) => { e.stopPropagation(); decideExit(d) }}>
                <kbd>{i + 1}</kbd> {t(`dEXIT_${d}` as Key)}
              </button>
            ))}
          </div>
        )}

        {entryDone && (
          <div className={`banner ${entryDone.state === 'ENTERED' ? 'accept' : 'pending'}`}>
            <div className="banner-title">{entryDone.tripNumber} — {t(`state${entryDone.state}` as Key)}</div>
            {entryDone.reviewReasons.map((r) => <div key={r}>• {reason(r)}</div>)}
            {!autoPrint && <button onClick={(e) => { e.stopPropagation(); print(entryDone.tripId) }}>{t('reprint')}</button>}
          </div>
        )}
        {exitDone && (
          <div className={`banner ${exitDone.state === 'CLOSED' || exitDone.state === 'EXITED_EMPTY' ? 'accept' : 'pending'}`}>
            <div className="banner-title">{exitDone.tripNumber} — {t(`state${exitDone.state}` as Key)}</div>
            {exitDone.reasons.map((r) => <div key={r}>• {reason(r)}</div>)}
          </div>
        )}

        {view && <button className="link" onClick={clear}>{t('nextTruck')}</button>}
      </div>

      <aside className="card inside" onClick={(e) => e.stopPropagation()}>
        <h3>{t('inside')} ({inside.length})</h3>
        {inside.length === 0 && <div className="muted">{t('noneInside')}</div>}
        {inside.map((c) => (
          <div key={c.tripId} className="inside-row">
            <div><strong>{c.fleetNumber ?? c.truckId}</strong> <span className="muted small code">{c.plate}</span></div>
            <div className="muted small">{c.tripNumber} · {fmtTime(c.enteredAt, lang)} · {t(`state${c.state}` as Key)}</div>
            <div className="row">
              <button className="small-btn" onClick={() => print(c.tripId)} disabled={busy}>{t('reprint')}</button>
              <button className="small-btn danger" onClick={() => manualExit(c)} disabled={busy}>{t('exitNoReceipt')}</button>
            </div>
          </div>
        ))}
      </aside>

      {zoom && view && (
        <div className="overlay" onClick={() => { setZoom(null); focus() }}>
          <div className="zoom">
            {zoom === 'photo'
              ? photoUrl && <img src={photoUrl} alt={t('cargoPhoto')} />
              : snapshots.filter((s) => s.cameraId === zoom.cameraId).map((s) => (
                <img key={s.id} src={`/api/snapshots/${s.id}`} alt={`${s.cameraLabel} ${s.frameIndex + 1}`} />
              ))}
          </div>
        </div>
      )}
    </div>
  )
}
