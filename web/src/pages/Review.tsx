import { useCallback, useEffect, useState } from 'react'
import { api, type ReviewQueueItem } from '../api'
import TripView from '../components/TripView'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

/** Supervisor: trips waiting for a decision, everything recorded about them, and labels for AI training. */
export default function ReviewPage({ onChanged }: { onChanged?: () => void }) {
  const { t, reason, lang } = useI18n()
  const [samples, setSamples] = useState(true)
  const [queue, setQueue] = useState<ReviewQueueItem[] | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [period, setPeriod] = useState(() => {
    const d = new Date().toISOString().slice(0, 10)
    return { from: d, to: d, videos: false }
  })
  const act = useAction()

  const loadQueue = useCallback(async () => setQueue(await api.get<ReviewQueueItem[]>(`/api/review/queue?samples=${samples}`)), [samples])
  useEffect(() => {
    act.run(loadQueue)
    const timer = setInterval(() => { loadQueue().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, [loadQueue]) // eslint-disable-line react-hooks/exhaustive-deps

  const changed = () => { loadQueue().catch(() => undefined); onChanged?.() }

  return (
    <div className="review-layout">
      <div className="stack">
        <Section title={`${t('queue')}${queue ? ` (${queue.length})` : ''}`} actions={
          <label className="inline small"><input type="checkbox" checked={samples} onChange={(e) => setSamples(e.target.checked)} /> {t('includeSamples')}</label>
        }>
          <ErrorBox error={act.error} />
          {queue === null && <div className="loading">{t('loading')}</div>}
          {queue?.length === 0 && <div className="empty"><strong>{t('reviewEmpty')}</strong></div>}
          <div className="queue">
            {queue?.map((q) => (
              <button key={q.tripId} className={`queue-item prio-${q.priority.toLowerCase()} ${q.tripId === selected ? 'active' : ''}`}
                onClick={() => setSelected(q.tripId)}>
                <div className="row-between">
                  <strong>{q.fleetNumber ?? '—'} · {q.tripNumber}</strong>
                  <span className="small">{q.isSample ? t('sample') : t(`priority${q.priority}` as Key)}</span>
                </div>
                <div className="small muted">
                  <span className="code">{q.plate ?? '—'}</span> · {q.driverName ?? '—'} · {fmtTime(q.enteredAt, lang)} · {t(q.exited ? 'exited' : 'notExited')}
                </div>
                <div className="small">{q.reasons.map((r) => reason(r)).join(' · ')}</div>
                <div className="label-row">
                  {q.truckStatus === 'PENDING_APPROVAL' && <span className="badge amber">{t('tsPENDING_APPROVAL')}</span>}
                  {q.investigating && <span className="badge warn">{t('investigating')}</span>}
                </div>
                {q.lastNote && <div className="small muted">“{q.lastNote}”</div>}
              </button>
            ))}
          </div>
        </Section>
        <Section title={t('dataset')}>
          <div className="row">
            <label>{t('from')}<input type="date" value={period.from} onChange={(e) => setPeriod({ ...period, from: e.target.value })} /></label>
            <label>{t('to')}<input type="date" value={period.to} onChange={(e) => setPeriod({ ...period, to: e.target.value })} /></label>
            <label className="inline small"><input type="checkbox" checked={period.videos}
              onChange={(e) => setPeriod({ ...period, videos: e.target.checked })} /> {t('datasetVideos')}</label>
            <a className="button" href={`/api/review/dataset?from=${period.from}&to=${period.to}&videos=${period.videos}`}>{t('download')}</a>
          </div>
        </Section>
      </div>

      <div>
        {selected
          ? <TripView key={selected} tripId={selected} editable onChanged={changed} />
          : <div className="card empty"><strong>{t('chooseTrip')}</strong></div>}
      </div>
    </div>
  )
}
