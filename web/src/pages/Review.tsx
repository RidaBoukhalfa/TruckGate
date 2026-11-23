import { useCallback, useEffect, useState } from 'react'
import { api, type ReviewQueueItem, type SnapshotDetail, type TripDetail } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

const loadLabels = ['FULL', 'PARTIAL', 'EMPTY', 'NOT_VISIBLE'] as const
const qualityLabels = ['GOOD', 'BLURRED', 'DARK', 'GLARE', 'OBSTRUCTED'] as const

/** Supervisor: trips waiting for a decision, everything recorded about them, and labels for AI training. */
export default function ReviewPage() {
  const { t, reason, lang } = useI18n()
  const [samples, setSamples] = useState(true)
  const [queue, setQueue] = useState<ReviewQueueItem[]>([])
  const [selected, setSelected] = useState<number | null>(null)
  const [detail, setDetail] = useState<TripDetail | null>(null)
  const [why, setWhy] = useState('')
  const [zoom, setZoom] = useState<SnapshotDetail | null>(null)
  const [period, setPeriod] = useState(() => {
    const d = new Date().toISOString().slice(0, 10)
    return { from: d, to: d }
  })
  const act = useAction()

  const loadQueue = useCallback(async () => setQueue(await api.get<ReviewQueueItem[]>(`/api/review/queue?samples=${samples}`)), [samples])
  const loadDetail = useCallback(async (id: number) => setDetail(await api.get<TripDetail>(`/api/trips/${id}`)), [])
  useEffect(() => { act.run(loadQueue) }, [loadQueue]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (selected) act.run(() => loadDetail(selected)); else setDetail(null) }, [selected]) // eslint-disable-line react-hooks/exhaustive-deps

  const decide = (decision: 'ACCEPT' | 'ACCEPT_PARTIAL' | 'REJECT') => act.run(async () => {
    if (!selected) return
    await api.post(`/api/review/trips/${selected}/decision`, { decision, reason: why || null })
    setWhy('')
    await loadQueue()
    await loadDetail(selected)
  })

  const label = (s: SnapshotDetail, type: 'LOAD' | 'PLATE' | 'QUALITY', value: string) => act.run(async () => {
    if (!value.trim()) return
    await api.post(`/api/review/snapshots/${s.id}/labels`, { type, value })
    if (selected) await loadDetail(selected)
  })

  const open = detail?.reviews.some((r) => r.decision === null) ?? false
  const ticketParts = detail?.ticket?.ticketId.match(/^Q(\d+)-(\d+)$/)

  return (
    <div className="review-layout">
      <Section title={t('queue')} actions={
        <label className="inline small"><input type="checkbox" checked={samples} onChange={(e) => setSamples(e.target.checked)} /> {t('includeSamples')}</label>
      }>
        {queue.length === 0 && <p className="muted">{t('reviewEmpty')}</p>}
        <div className="queue">
          {queue.map((q) => (
            <button key={q.tripId} className={`queue-item prio-${q.priority.toLowerCase()} ${q.tripId === selected ? 'active' : ''}`}
              onClick={() => setSelected(q.tripId)}>
              <div className="row-between">
                <strong>{q.fleetNumber ?? '—'} · {q.tripNumber}</strong>
                <span className="small">{q.isSample ? t('sample') : t(`priority${q.priority}` as Key)}</span>
              </div>
              <div className="small muted">{q.plate} · {q.driverName} · {fmtTime(q.enteredAt, lang)} · {t(q.exited ? 'exited' : 'notExited')}</div>
              <div className="small">{q.reasons.map((r) => reason(r)).join(' · ')}</div>
            </button>
          ))}
        </div>
        <hr />
        <h3>{t('dataset')}</h3>
        <div className="row">
          <label>{t('from')}<input type="date" value={period.from} onChange={(e) => setPeriod({ ...period, from: e.target.value })} /></label>
          <label>{t('to')}<input type="date" value={period.to} onChange={(e) => setPeriod({ ...period, to: e.target.value })} /></label>
          <a className="button" href={`/api/review/dataset?from=${period.from}&to=${period.to}`}>{t('download')}</a>
        </div>
      </Section>

      <div className="stack">
        <ErrorBox error={act.error} />
        {!detail && <div className="card muted">{t('chooseTrip')}</div>}
        {detail && (
          <>
            <Section title={`${detail.trip.tripNumber} — ${detail.trip.fleetNumber ?? detail.trip.truckId}`}
              actions={<span className="badge">{t(`state${detail.trip.state}` as Key)}</span>}>
              <dl className="facts">
                <dt>{t('plate')}</dt><dd className="ltr">{detail.trip.plate ?? '—'}</dd>
                <dt>{t('driver')}</dt><dd>{detail.trip.driverName ?? '—'}</dd>
                <dt>{t('entered')}</dt><dd>{fmtTime(detail.trip.enteredAt, lang)}</dd>
                <dt>{t('load')}</dt><dd>{detail.trip.loadStatus ? t(`d${detail.trip.loadStatus}` as Key) : '—'}</dd>
                <dt>{t('receiptPrints')}</dt><dd>{detail.receiptPrints}</dd>
                {detail.ticket && (
                  <>
                    <dt>{t('ticket')}</dt><dd>{detail.ticket.ticketId} · {t('issued')} {fmtTime(detail.ticket.issuedAt, lang)}</dd>
                    <dt>{t('operator')}</dt><dd>{detail.ticket.operatorName ?? '—'}</dd>
                    <dt>{t('gps')}</dt>
                    <dd className="ltr">{detail.ticket.lat !== null ? `${detail.ticket.lat.toFixed(5)}, ${detail.ticket.lon?.toFixed(5)}` : reason('NO_GPS')}</dd>
                    <dt>{t('clockDelta')}</dt><dd>{detail.ticket.clockDelta} {t('seconds')}</dd>
                    <dt>{t('firstSeenVia')}</dt><dd>{t(`via${detail.ticket.firstSeenVia}` as Key)}</dd>
                    {detail.ticket.driverChangeReason && <><dt>{t('driverChangeReason')}</dt><dd>{detail.ticket.driverChangeReason}</dd></>}
                  </>
                )}
              </dl>
              {detail.ticket?.voided && <div className="error">{t('voidedTicket')}</div>}
              <h3>{t('quarryPhoto')}</h3>
              {detail.ticket?.hasPhoto && ticketParts
                ? <img className="quarry-photo" src={`/api/tickets/${ticketParts[1]}/${Number(ticketParts[2])}/photo`} alt={t('quarryPhoto')} />
                : <p className="muted">{t('noQuarryPhoto')}</p>}
            </Section>

            {detail.scans.map((sc) => (
              <Section key={sc.scanId} title={`${t(`dir${sc.direction}` as Key)} · ${fmtTime(sc.scannedAt, lang)} · ${sc.gateName ?? ''}`}
                actions={<span className={`badge ${sc.outcome.toLowerCase()}`}>{sc.outcome}</span>}>
                <div className="small">
                  {[...sc.reasons, ...sc.notes].map((r) => reason(r)).join(' · ')}
                  {sc.decision && <> — <strong>{t(`d${sc.decision}` as Key)}</strong></>}
                  {sc.decisionNote && <> ({sc.decisionNote})</>}
                  {sc.gatekeeperName && <span className="muted"> · {sc.gatekeeperName}</span>}
                </div>
                <div className="pictures labeled">
                  {detail.snapshots.filter((s) => s.scanId === sc.scanId).map((s) => (
                    <figure key={s.id}>
                      <img src={`/api/snapshots/${s.id}`} alt={s.cameraLabel} onClick={() => setZoom(s)} />
                      <figcaption>{s.cameraLabel} · {t(`role${s.role}` as Key)} #{s.frameIndex + 1}</figcaption>
                      <div className="label-row">
                        {(s.role === 'CARGO' || s.role === 'OVERVIEW') && loadLabels.map((l) => (
                          <button key={l} className={`chip ${s.loadLabel === l ? 'on' : ''}`} onClick={() => label(s, 'LOAD', l)}>{t(`l${l}` as Key)}</button>
                        ))}
                        {s.role === 'PLATE' && (
                          <input className="plate-input" placeholder={t('labelPlate')} defaultValue={s.plateLabel ?? ''}
                            onKeyDown={(e) => { if (e.key === 'Enter') label(s, 'PLATE', (e.target as HTMLInputElement).value) }}
                            onBlur={(e) => { if (e.target.value && e.target.value !== s.plateLabel) label(s, 'PLATE', e.target.value) }} />
                        )}
                        <select value={s.qualityLabel ?? ''} onChange={(e) => label(s, 'QUALITY', e.target.value)}>
                          <option value="">{t('labelQuality')}</option>
                          {qualityLabels.map((q) => <option key={q} value={q}>{t(`q${q}` as Key)}</option>)}
                        </select>
                      </div>
                    </figure>
                  ))}
                </div>
              </Section>
            ))}

            {open && (
              <Section title={t('decide')}>
                <textarea rows={2} className="wide-full" placeholder={t('decisionReason')} value={why} onChange={(e) => setWhy(e.target.value)} />
                <div className="row">
                  <button className="primary" disabled={act.busy} onClick={() => decide('ACCEPT')}>{t('accept')}</button>
                  <button disabled={act.busy} onClick={() => decide('ACCEPT_PARTIAL')}>{t('acceptPartial')}</button>
                  <button className="danger" disabled={act.busy || !why.trim()} onClick={() => decide('REJECT')}>{t('reject')}</button>
                </div>
              </Section>
            )}

            <Section title={t('reviews')}>
              <table>
                <tbody>
                  {detail.reviews.map((r) => (
                    <tr key={r.id}>
                      <td>{fmtTime(r.createdAt, lang)}</td>
                      <td>{r.isSample ? t('sample') : r.reasons.map((x) => reason(x)).join(' · ')}</td>
                      <td>{r.decision ?? '…'} {r.decisionReason && `— ${r.decisionReason}`}</td>
                      <td className="muted">{r.decidedByName} {fmtTime(r.decidedAt, lang)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <h3>{t('history')}</h3>
              <ol className="timeline">
                {detail.events.map((e, i) => (
                  <li key={i}>
                    <span className="muted small">{fmtTime(e.at, lang)}</span> {t(`state${e.toState}` as Key)}
                    <span className="muted small"> · {reason(e.reasonCode)} · {e.actor}{e.note ? ` · ${e.note}` : ''}</span>
                  </li>
                ))}
              </ol>
            </Section>
          </>
        )}
      </div>

      {zoom && (
        <div className="overlay" onClick={() => setZoom(null)}>
          <div className="zoom"><img src={`/api/snapshots/${zoom.id}`} alt={zoom.cameraLabel} /></div>
        </div>
      )}
    </div>
  )
}
