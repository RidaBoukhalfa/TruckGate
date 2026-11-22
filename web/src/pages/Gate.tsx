import { useCallback, useEffect, useRef, useState } from 'react'
import { api, type DecisionResult, type Gate, type ScanView, type Snapshot } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, useAction } from '../ui'

const decisions = ['FULL', 'PARTIAL', 'EMPTY', 'DIFFERENT_TRUCK', 'PLATE_UNREADABLE'] as const

/**
 * Gatekeeper station. The scanner (USB, keyboard mode, Enter at the end) types into the always-focused
 * field. The server checks the ticket, burns it and the selected cameras take their pictures at once.
 * Decisions use keys 1–5; Esc clears the screen for the next truck.
 */
export default function GatePage() {
  const { t, reason, lang } = useI18n()
  const [gates, setGates] = useState<Gate[]>([])
  const [gateId, setGateId] = useState<number | null>(() => Number(localStorage.getItem('tg.gate')) || null)
  const [input, setInput] = useState('')
  const [view, setView] = useState<ScanView | null>(null)
  const [decided, setDecided] = useState<DecisionResult | null>(null)
  const [zoom, setZoom] = useState<Snapshot | null>(null)
  const { busy, error, setError, run } = useAction()
  const field = useRef<HTMLInputElement>(null)

  useEffect(() => {
    api.get<Gate[]>('/api/gate/gates').then((g) => {
      setGates(g)
      if (g.length === 1) setGateId(g[0].id)
    }).catch(() => setGates([]))
  }, [])
  useEffect(() => { if (gateId) localStorage.setItem('tg.gate', String(gateId)) }, [gateId])

  const focus = useCallback(() => setTimeout(() => field.current?.focus(), 0), [])
  const clear = useCallback(() => {
    setView(null); setDecided(null); setZoom(null); setError(null); setInput(''); focus()
  }, [focus, setError])

  const scan = async (text: string) => {
    if (!gateId || !text.trim()) return
    setView(null); setDecided(null); setZoom(null)
    const v = await run(() => api.post<ScanView>('/api/gate/scan', { gateId, qrText: text.trim() }))
    setInput('')
    if (v) setView(v)
    focus()
  }

  const decide = useCallback(async (d: (typeof decisions)[number]) => {
    if (!view?.scan.tripId || decided) return
    const r = await run(() => api.post<DecisionResult>(`/api/gate/scans/${view.scan.scanId}/decision`, { decision: d }))
    if (r) {
      setDecided(r)
      setTimeout(clear, 4000)
    }
    focus()
  }, [view, decided, run, clear, focus])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { clear(); return }
      if (input === '' && view?.scan.tripId && !decided && e.key >= '1' && e.key <= '5') {
        e.preventDefault()
        decide(decisions[Number(e.key) - 1])
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [view, decided, input, decide, clear])

  if (!gateId) {
    return (
      <div className="card">
        <h2>{t('chooseGate')}</h2>
        <div className="row">{gates.map((g) => <button key={g.id} className="primary" onClick={() => setGateId(g.id)}>{g.name}</button>)}</div>
      </div>
    )
  }

  const scanRes = view?.scan
  const firstPerCamera = view ? view.snapshots.filter((s) => s.frameIndex === 0) : []

  return (
    <div className="gate" onClick={() => { if (!zoom) focus() }}>
      <div className="scanbar">
        <input
          ref={field}
          autoFocus
          className="scaninput"
          placeholder={busy ? t('scanning') : t('scanHere')}
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); scan(input) } }}
          onBlur={() => { if (!zoom) focus() }}
          disabled={busy}
        />
        <span className="muted">{gates.find((g) => g.id === gateId)?.name}</span>
      </div>
      <ErrorBox error={error} />

      {scanRes && (
        <div className={`banner ${scanRes.outcome.toLowerCase()}`}>
          <div className="banner-title">{t(`outcome${scanRes.outcome}` as Key)}</div>
          {scanRes.reasons.map((r) => <div key={r}>• {reason(r)}</div>)}
          {scanRes.notes.map((r) => <div key={r} className="note">ⓘ {reason(r)}</div>)}
        </div>
      )}

      {view && (
        <div className="gate-grid">
          <div className="card ticketinfo">
            {scanRes?.ticket ? (
              <>
                <div className="big">{view.names?.fleetNumber ?? scanRes.ticket.truckId}</div>
                <div className="plate">{view.names?.plate ?? '—'}</div>
                <dl>
                  <dt>{t('ticket')}</dt><dd>{scanRes.ticket.ticketId} · {scanRes.ticket.shortCode}</dd>
                  <dt>{t('driver')}</dt>
                  <dd>{view.names?.driverName ?? scanRes.ticket.driverId}{' '}
                    <span className={view.names?.driverIsDefault ? 'ok' : 'warn'}>
                      ({t(view.names?.driverIsDefault ? 'usualDriver' : 'otherDriver')})
                    </span>
                  </dd>
                  <dt>{t('operator')}</dt><dd>{view.names?.operatorName ?? scanRes.ticket.operatorId}</dd>
                  <dt>{t('issued')}</dt>
                  <dd>{new Date(scanRes.ticket.issuedAt * 1000).toLocaleTimeString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ')} ·{' '}
                    {t('minutesAgo', { n: Math.round(scanRes.ticket.ageSeconds / 60) })}</dd>
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

      {scanRes?.tripId && !decided && (
        <div className="decisions">
          <span>{t('decide')}:</span>
          {decisions.map((d, i) => (
            <button key={d} className={d === 'FULL' || d === 'PARTIAL' ? 'primary' : 'danger'} disabled={busy}
              onClick={(e) => { e.stopPropagation(); decide(d) }}>
              <kbd>{i + 1}</kbd> {t(`d${d}` as Key)}
            </button>
          ))}
        </div>
      )}

      {decided && (
        <div className={`banner ${decided.state === 'ENTERED' ? 'accept' : 'pending'}`}>
          <div className="banner-title">{decided.tripNumber} — {t(`state${decided.state}` as Key)}</div>
          {decided.reviewReasons.map((r) => <div key={r}>• {reason(r)}</div>)}
        </div>
      )}

      {view && <button className="link" onClick={clear}>{t('nextTruck')}</button>}

      {zoom && view && (
        <div className="overlay" onClick={() => { setZoom(null); focus() }}>
          <div className="zoom">
            {view.snapshots.filter((s) => s.cameraId === zoom.cameraId).map((s) => (
              <img key={s.id} src={`/api/snapshots/${s.id}`} alt={`${s.cameraLabel} ${s.frameIndex + 1}`} />
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
