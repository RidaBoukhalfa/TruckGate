import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import {
  api, type DecisionResult, type ExitDecisionResult, type ExitView, type Gate, type ReceiptData, type ScanView, type Snapshot,
  type TripCard,
} from '../api'
import { useI18n, type Key } from '../i18n'
import { printReceipt } from '../print'
import { ErrorBox, fmtTime, useAction } from '../ui'

const entryDecisions = ['FULL', 'PARTIAL', 'EMPTY', 'DIFFERENT_TRUCK', 'PLATE_UNREADABLE'] as const
const exitDecisions = ['EMPTY', 'LOADED'] as const

type View = ScanView | ExitView

/**
 * Gatekeeper station. The scanner (USB, keyboard mode, Enter at the end) types into the always-focused
 * field. A quarry ticket (TG1:) is an entry, the gate receipt (TR1:) is an exit; the server checks it and
 * the selected cameras take their pictures at once. Keys 1–5 decide an entry, 1–2 an exit; Esc clears.
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
  const [zoom, setZoom] = useState<Snapshot | null>(null)
  const [inside, setInside] = useState<TripCard[]>([])
  const [manual, setManual] = useState({ open: false, ticketId: '', code: '' })
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

  const focus = useCallback(() => setTimeout(() => { if (!manual.open) field.current?.focus() }, 0), [manual.open])
  const clear = useCallback(() => {
    setView(null); setEntryDone(null); setExitDone(null); setZoom(null); setError(null); setInput(''); focus()
  }, [focus, setError])

  const show = (v: View | undefined) => {
    setInput('')
    if (v) setView(v)
    focus()
  }

  const scan = async (text: string) => {
    if (!gateId || !text.trim()) return
    setView(null); setEntryDone(null); setExitDone(null); setZoom(null)
    show(await run(() => api.post<View>('/api/gate/scan', { gateId, qrText: text.trim() })))
  }

  const manualEntry = async (e: FormEvent) => {
    e.preventDefault()
    if (!gateId) return
    setView(null); setEntryDone(null); setExitDone(null)
    const v = await run(() => api.post<ScanView>('/api/gate/manual', { gateId, ticketId: manual.ticketId, shortCode: manual.code }))
    if (v) setManual({ open: false, ticketId: '', code: '' })
    show(v)
  }

  const manualExit = async (c: TripCard) => {
    if (!gateId || !window.confirm(t('confirmExitNoReceipt', { truck: c.fleetNumber ?? c.tripNumber }))) return
    setView(null); setEntryDone(null); setExitDone(null)
    show(await run(() => api.post<ExitView>('/api/gate/exit/manual', { gateId, tripId: c.tripId })))
  }

  const print = useCallback(async (tripId: number) => {
    const r = await run(() => api.post<ReceiptData>(`/api/gate/trips/${tripId}/receipt`))
    if (r) await printReceipt(r, i18n)
  }, [run, i18n])

  const decideEntry = useCallback(async (d: (typeof entryDecisions)[number]) => {
    if (view?.kind !== 'ENTRY' || !view.scan.tripId || entryDone) return
    const r = await run(() => api.post<DecisionResult>(`/api/gate/scans/${view.scan.scanId}/decision`, { decision: d }))
    if (r) {
      setEntryDone(r)
      loadInside()
      if (autoPrint) await print(r.tripId)
      setTimeout(clear, 5000)
    }
    focus()
  }, [view, entryDone, run, clear, focus, autoPrint, print, loadInside])

  const decideExit = useCallback(async (d: (typeof exitDecisions)[number]) => {
    if (view?.kind !== 'EXIT' || !view.exit.trip || view.exit.outcome === 'REJECT' || exitDone) return
    const r = await run(() => api.post<ExitDecisionResult>(`/api/gate/exit/scans/${view.exit.scanId}/decision`, { decision: d }))
    if (r) {
      setExitDone(r)
      loadInside()
      setTimeout(clear, 4000)
    }
    focus()
  }, [view, exitDone, run, clear, focus, loadInside])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (manual.open) return
      if (e.key === 'Escape') { clear(); return }
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
        <div className="row">{gates.map((g) => <button key={g.id} className="primary" onClick={() => setGateId(g.id)}>{g.name}</button>)}</div>
      </div>
    )
  }

  const snapshots = view?.snapshots ?? []
  const firstPerCamera = snapshots.filter((s) => s.frameIndex === 0)
  const outcome = view?.kind === 'ENTRY' ? view.scan.outcome : view?.kind === 'EXIT' ? view.exit.outcome : null
  const reasons = view?.kind === 'ENTRY' ? view.scan.reasons : view?.kind === 'EXIT' ? view.exit.reasons : []
  const notes = view?.kind === 'ENTRY' ? view.scan.notes : view?.kind === 'EXIT' ? view.exit.notes : []

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
          />
          <span className="muted">{gates.find((g) => g.id === gateId)?.name}</span>
        </div>
        <div className="row small">
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

        {view && outcome && (
          <div className={`banner ${outcome.toLowerCase()}`}>
            <div className="banner-title">
              {view.kind === 'EXIT' ? t(`outcomeExit${outcome}` as Key) : t(`outcome${outcome}` as Key)}
            </div>
            {reasons.map((r) => <div key={r}>• {reason(r)}</div>)}
            {notes.map((r) => <div key={r} className="note">ⓘ {reason(r)}</div>)}
          </div>
        )}

        {view && (
          <div className="gate-grid">
            <div className="card ticketinfo">
              {view.kind === 'ENTRY' ? (view.scan.ticket ? (
                <>
                  <div className="big">{view.names?.fleetNumber ?? view.scan.ticket.truckId}</div>
                  <div className="plate">{view.names?.plate ?? '—'}</div>
                  <dl>
                    <dt>{t('ticket')}</dt><dd>{view.scan.ticket.ticketId} · {view.scan.ticket.shortCode}</dd>
                    <dt>{t('driver')}</dt>
                    <dd>{view.names?.driverName ?? view.scan.ticket.driverId}{' '}
                      <span className={view.names?.driverIsDefault ? 'ok' : 'warn'}>
                        ({t(view.names?.driverIsDefault ? 'usualDriver' : 'otherDriver')})
                      </span>
                    </dd>
                    <dt>{t('operator')}</dt><dd>{view.names?.operatorName ?? view.scan.ticket.operatorId}</dd>
                    <dt>{t('issued')}</dt>
                    <dd>{new Date(view.scan.ticket.issuedAt * 1000).toLocaleTimeString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ')} ·{' '}
                      {t('minutesAgo', { n: Math.round(view.scan.ticket.ageSeconds / 60) })}</dd>
                  </dl>
                </>
              ) : <div className="muted">{t('none')}</div>) : view.exit.trip ? (
                <>
                  <div className="big">{view.exit.trip.fleetNumber ?? view.exit.trip.truckId}</div>
                  <div className="plate">{view.exit.trip.plate ?? '—'}</div>
                  <dl>
                    <dt>{t('trip')}</dt><dd>{view.exit.trip.tripNumber} · {view.exit.trip.ticketId}</dd>
                    <dt>{t('driver')}</dt><dd>{view.exit.trip.driverName ?? '—'}</dd>
                    <dt>{t('entered')}</dt>
                    <dd>{fmtTime(view.exit.trip.enteredAt, lang)} · {t('minutesAgo', { n: Math.round((Date.now() - Date.parse(view.exit.trip.enteredAt)) / 60000) })}</dd>
                    <dt>{t('entryDecision')}</dt><dd>{view.exit.trip.entryDecision ? t(`d${view.exit.trip.entryDecision}` as Key) : '—'}</dd>
                  </dl>
                </>
              ) : <div className="muted">{t('none')}</div>}
            </div>

            <div className="card">
              <h3>{t('pictures')}</h3>
              {firstPerCamera.length === 0 && <div className="warn">{t('noPictures')}</div>}
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
        )}

        {view?.kind === 'ENTRY' && view.scan.tripId && !entryDone && (
          <div className="decisions">
            <span>{t('decide')}:</span>
            {entryDecisions.map((d, i) => (
              <button key={d} className={d === 'FULL' || d === 'PARTIAL' ? 'primary' : 'danger'} disabled={busy}
                onClick={(e) => { e.stopPropagation(); decideEntry(d) }}>
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
            <div><strong>{c.fleetNumber ?? c.truckId}</strong> <span className="muted small">{c.plate}</span></div>
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
            {snapshots.filter((s) => s.cameraId === zoom.cameraId).map((s) => (
              <img key={s.id} src={`/api/snapshots/${s.id}`} alt={`${s.cameraLabel} ${s.frameIndex + 1}`} />
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
