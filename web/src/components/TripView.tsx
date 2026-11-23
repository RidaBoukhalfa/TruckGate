import { useCallback, useEffect, useState } from 'react'
import { api, type SnapshotDetail, type TripDetail, type VideoItem } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

const loadLabels = ['FULL', 'PARTIAL', 'EMPTY', 'NOT_VISIBLE'] as const
const qualityLabels = ['GOOD', 'BLURRED', 'DARK', 'GLARE', 'OBSTRUCTED'] as const
export const tripLabels = ['NORMAL_TRIP', 'EMPTY_TRUCK', 'LOADED_TRUCK', 'INCOMPLETE_CARGO', 'WRONG_TRUCK', 'PLATE_UNREADABLE',
  'POSSIBLE_MISMATCH', 'CAMERA_FAILURE', 'NEEDS_REVIEW'] as const

/** Quarry photo address from a ticket id such as Q3-000127. */
export function ticketPhotoUrl(ticketId: string | null | undefined): string | null {
  const m = ticketId?.match(/^Q(\d+)-(\d+)$/)
  return m ? `/api/tickets/${m[1]}/${Number(m[2])}/photo` : null
}

const fmtDuration = (ms: number | null) => (ms === null ? '' : `${Math.round(ms / 1000)} s`)
const fmtSize = (b: number | null) => (b === null ? '' : b > 1_048_576 ? `${(b / 1_048_576).toFixed(1)} MB` : `${Math.round(b / 1024)} KB`)

/** One recorded passage clip, or the honest reason there is none. */
export function VideoBox({ v }: { v: VideoItem }) {
  const { t, reason } = useI18n()
  const head = `${v.cameraLabel} · ${t(`dir${v.direction}` as Key)}`
  if (v.status === 'OK' || v.status === 'PARTIAL') {
    return (
      <figure className="video-fig">
        <video controls preload="metadata" src={`/api/videos/${v.id}`} />
        <figcaption>
          {head} · {fmtDuration(v.durationMs)} · {fmtSize(v.sizeBytes)}
          {v.status === 'PARTIAL' && <span className="badge warn">{t('vPARTIAL')}</span>}
        </figcaption>
      </figure>
    )
  }
  return (
    <div className={v.status === 'FAILED' ? 'video-missing' : 'video-wait'}>
      <strong>{head}</strong>
      <div>{t(`v${v.status}` as Key)}{v.error ? ` — ${reason(v.error)}` : ''}</div>
    </div>
  )
}

/**
 * Everything recorded about one trip: ticket, quarry photo, scans with pictures and video, notes,
 * training labels, reviews and the state history. With `editable` the supervisor can decide, ask for an
 * investigation, add notes and label pictures and the trip.
 */
export default function TripView({ tripId, editable = false, onChanged }: {
  tripId: number; editable?: boolean; onChanged?: () => void
}) {
  const { t, reason, lang } = useI18n()
  const [d, setD] = useState<TripDetail | null>(null)
  const [why, setWhy] = useState('')
  const [note, setNote] = useState('')
  const [labels, setLabels] = useState<string[]>([])
  const [labelNote, setLabelNote] = useState('')
  const [zoom, setZoom] = useState<SnapshotDetail | null>(null)
  const act = useAction()

  const load = useCallback(async () => {
    const x = await api.get<TripDetail>(`/api/trips/${tripId}`)
    setD(x)
    const last = x.annotations[0]
    setLabels(last?.labels ?? [])
    setLabelNote(last?.note ?? '')
  }, [tripId])
  useEffect(() => { setD(null); act.run(load) }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  const after = async () => { await load(); onChanged?.() }
  const decide = (decision: 'ACCEPT' | 'ACCEPT_PARTIAL' | 'REJECT') => act.run(async () => {
    await api.post(`/api/review/trips/${tripId}/decision`, { decision, reason: why.trim() || null })
    setWhy('')
    await after()
  })
  const investigate = () => act.run(async () => {
    await api.post(`/api/review/trips/${tripId}/investigate`, { note: why })
    setWhy('')
    await after()
  })
  const addNote = () => act.run(async () => {
    await api.post(`/api/review/trips/${tripId}/notes`, { note })
    setNote('')
    await load()
  })
  const saveLabels = () => act.run(async () => {
    await api.post(`/api/review/trips/${tripId}/annotations`, { labels, note: labelNote.trim() || null })
    await load()
  })
  const label = (s: SnapshotDetail, type: 'LOAD' | 'PLATE' | 'QUALITY', value: string) => act.run(async () => {
    if (!value.trim()) return
    await api.post(`/api/review/snapshots/${s.id}/labels`, { type, value })
    await load()
  })

  if (!d) return <>{act.error ? <ErrorBox error={act.error} /> : <div className="loading">{t('loading')}</div>}</>

  const open = d.reviews.some((r) => r.decision === null)
  const photo = d.ticket?.hasPhoto ? ticketPhotoUrl(d.ticket.ticketId) : null
  const current = d.annotations[0]
  const savedLabels = (current?.labels ?? []).slice().sort().join(',')
  const labelsChanged = labels.slice().sort().join(',') !== savedLabels || (labelNote.trim() || null) !== (current?.note ?? null)

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <Section title={`${d.trip.tripNumber} — ${d.trip.fleetNumber ?? d.trip.truckId}`} actions={
        <div className="row-between">
          {d.truck && d.truck.approvalStatus !== 'APPROVED' && (
            <span className="badge amber">{t(`ts${d.truck.approvalStatus}` as Key)}</span>
          )}
          <span className="badge">{t(`state${d.trip.state}` as Key)}</span>
        </div>
      }>
        <div className="trip-head">
          <dl className="facts">
            <dt>{t('plate')}</dt><dd className="code">{d.trip.plate ?? d.truck?.reportedPlate ?? '—'}</dd>
            {d.truck && (d.truck.ownerLastName || d.truck.ownerFirstName) && (
              <><dt>{t('ownerLastName')} / {t('ownerFirstName')}</dt><dd>{d.truck.ownerLastName} {d.truck.ownerFirstName}</dd></>
            )}
            <dt>{t('driver')}</dt><dd><bdi>{d.trip.driverName ?? '—'}</bdi></dd>
            <dt>{t('entered')}</dt><dd>{fmtTime(d.trip.enteredAt, lang)}</dd>
            <dt>{t('load')}</dt><dd>{d.trip.loadStatus ? t(`d${d.trip.loadStatus}` as Key) : '—'}</dd>
            <dt>{t('receiptPrints')}</dt><dd>{d.receiptPrints}</dd>
            {d.ticket && (
              <>
                <dt>{t('ticket')}</dt><dd><span className="code">{d.ticket.ticketId}</span> · {t('issued')} {fmtTime(d.ticket.issuedAt, lang)}</dd>
                <dt>{t('operator')}</dt><dd><bdi>{d.ticket.operatorName ?? '—'}</bdi></dd>
                <dt>{t('gps')}</dt>
                <dd className="ltr">{d.ticket.lat !== null ? `${d.ticket.lat.toFixed(5)}, ${d.ticket.lon?.toFixed(5)}` : reason('NO_GPS')}</dd>
                <dt>{t('clockDelta')}</dt><dd>{d.ticket.clockDelta} {t('seconds')}</dd>
                <dt>{t('firstSeenVia')}</dt><dd>{t(`via${d.ticket.firstSeenVia}` as Key)}</dd>
                {d.ticket.driverChangeReason && <><dt>{t('driverChangeReason')}</dt><dd>{d.ticket.driverChangeReason}</dd></>}
              </>
            )}
          </dl>
          <div>
            <h3>{t('cargoPhoto')}</h3>
            {photo
              ? <img className="cargo-photo" src={photo} alt={t('cargoPhoto')} />
              : <div className="muted small">{d.ticket && !d.ticket.hasPhoto ? t('photoNone') : t('noQuarryPhoto')}</div>}
          </div>
        </div>
        {d.ticket?.voided && <div className="error">{t('voidedTicket')}</div>}
      </Section>

      {editable && open && (
        <Section title={t('supervisorDecision')}>
          <textarea rows={2} className="wide-full" placeholder={t('decisionReason')} value={why} onChange={(e) => setWhy(e.target.value)} />
          <div className="row">
            <button className="primary" disabled={act.busy} onClick={() => decide('ACCEPT')}>{t('accept')}</button>
            <button disabled={act.busy} onClick={() => decide('ACCEPT_PARTIAL')}>{t('acceptPartial')}</button>
            <button className="danger" disabled={act.busy || !why.trim()} onClick={() => decide('REJECT')}>{t('reject')}</button>
            <span className="spacer" />
            <button disabled={act.busy || !why.trim()} onClick={investigate} title={t('investigateNote')}>{t('investigate')}</button>
          </div>
        </Section>
      )}

      {d.scans.map((sc) => {
        const videos = d.videos.filter((v) => v.scanId === sc.scanId)
        const pictures = d.snapshots.filter((s) => s.scanId === sc.scanId)
        return (
          <Section key={sc.scanId} title={`${t(`dir${sc.direction}` as Key)} · ${fmtTime(sc.scannedAt, lang)} · ${sc.gateName ?? ''}`}
            actions={<span className={`badge ${sc.outcome.toLowerCase()}`}>{t(`outcome${sc.outcome}` as Key)}</span>}>
            <div className="small">
              {[...sc.reasons, ...sc.notes].map((r) => reason(r)).join(' · ')}
              {sc.decision && <> — <strong>{t(`d${sc.decision}` as Key)}</strong></>}
              {sc.decisionNote && <> ({sc.decisionNote})</>}
              {sc.gatekeeperName && <span className="muted"> · {sc.gatekeeperName}</span>}
            </div>
            {videos.length > 0 && (
              <>
                <h3>{t('videos')}</h3>
                <div className="videos">{videos.map((v) => <VideoBox key={v.id} v={v} />)}</div>
              </>
            )}
            {pictures.length > 0 && <h3>{t('pictures')}</h3>}
            <div className="pictures labeled">
              {pictures.map((s) => (
                <figure key={s.id}>
                  <img src={`/api/snapshots/${s.id}`} alt={s.cameraLabel} onClick={() => setZoom(s)} />
                  <figcaption>{s.cameraLabel} · {t(`role${s.role}` as Key)} #{s.frameIndex + 1}</figcaption>
                  {editable && (
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
                  )}
                </figure>
              ))}
            </div>
          </Section>
        )
      })}

      <Section title={t('annotations')}>
        <p className="muted small">{t('annotationHelp')}</p>
        <div className="label-row">
          {tripLabels.map((l) => (
            <button key={l} className={`chip ${labels.includes(l) ? 'on' : ''}`} disabled={!editable}
              onClick={() => setLabels((x) => (x.includes(l) ? x.filter((y) => y !== l) : [...x, l]))}>
              {t(`tl${l}` as Key)}
            </button>
          ))}
        </div>
        {editable && (
          <div className="row">
            <input className="wide" placeholder={t('note')} value={labelNote} onChange={(e) => setLabelNote(e.target.value)} />
            <button className="primary" disabled={act.busy || !labelsChanged} onClick={saveLabels}>{t('saveLabels')}</button>
          </div>
        )}
        {current && (
          <div className="muted small">{current.annotatedByName} · {fmtTime(current.annotatedAt, lang)}
            {d.annotations.length > 1 && ` · ${t('history')}: ${d.annotations.length}`}</div>
        )}
      </Section>

      <Section title={t('notes')}>
        {d.notes.length === 0 && <div className="muted small">{t('none')}</div>}
        {d.notes.map((n) => (
          <div key={n.id} className="note-item">
            {n.kind === 'INVESTIGATE' && <span className="badge warn">{t('investigating')}</span>} {n.note}
            <div className="muted small">{n.createdByName} · {fmtTime(n.createdAt, lang)}</div>
          </div>
        ))}
        {editable && (
          <div className="row">
            <input className="wide" value={note} onChange={(e) => setNote(e.target.value)} placeholder={t('addNote')} />
            <button disabled={act.busy || !note.trim()} onClick={addNote}>{t('addNote')}</button>
          </div>
        )}
      </Section>

      <Section title={t('reviews')}>
        {d.reviews.length === 0 && <div className="muted small">{t('none')}</div>}
        <table>
          <tbody>
            {d.reviews.map((r) => (
              <tr key={r.id}>
                <td className="small">{fmtTime(r.createdAt, lang)}</td>
                <td>{r.isSample ? t('sample') : r.reasons.map((x) => reason(x)).join(' · ')}</td>
                <td>{r.decision ? reason(`REVIEW_${r.decision}`) : '…'} {r.decisionReason && `— ${r.decisionReason}`}</td>
                <td className="muted small">{r.decidedByName} {r.decidedAt ? fmtTime(r.decidedAt, lang) : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <h3>{t('history')}</h3>
        <ol className="timeline">
          {d.events.map((e, i) => (
            <li key={i}>
              <span className="muted small">{fmtTime(e.at, lang)}</span> {t(`state${e.toState}` as Key)}
              <span className="muted small"> · {reason(e.reasonCode)} · {e.actor}{e.note ? ` · ${e.note}` : ''}</span>
            </li>
          ))}
        </ol>
      </Section>

      {zoom && (
        <div className="overlay" onClick={() => setZoom(null)}>
          <div className="zoom"><img src={`/api/snapshots/${zoom.id}`} alt={zoom.cameraLabel} /></div>
        </div>
      )}
    </div>
  )
}
