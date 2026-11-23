import { useCallback, useEffect, useState } from 'react'
import { api, type Dashboard } from '../api'
import type { Tab } from '../App'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

type Tile = { key: Key; value: number; tone?: 'ok' | 'warn' | 'bad' | ''; open?: Tab }

/** Supervisor / manager: today at a glance, refreshed every 30 seconds. Tiles open the page that handles them. */
export default function DashboardPage({ onOpen, canOpen }: { onOpen?: (tab: Tab) => void; canOpen?: (tab: Tab) => boolean }) {
  const { t, reason, lang } = useI18n()
  const [d, setD] = useState<Dashboard | null>(null)
  const act = useAction()

  const load = useCallback(async () => setD(await api.get<Dashboard>('/api/reports/dashboard')), [])
  useEffect(() => {
    act.run(load)
    const timer = setInterval(() => { load().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  if (!d) return act.error ? <ErrorBox error={act.error} /> : <div className="loading">{t('loading')}</div>

  const flag = (n: number, tone: 'warn' | 'bad') => (n > 0 ? tone : '')
  const today: Tile[] = [
    { key: 'kTripsToday', value: d.tripsToday, open: 'receipts' },
    { key: 'kCompleted', value: d.completedToday, tone: 'ok', open: 'receipts' },
    { key: 'kInside', value: d.inside },
    { key: 'kBurnedToday', value: d.burnedToday, open: 'receipts' },
    { key: 'kEntries', value: d.entries }, { key: 'kExits', value: d.exits },
    { key: 'kPartial', value: d.partial },
    { key: 'kRejected', value: d.rejected, tone: flag(d.rejected, 'bad') },
  ]
  const attention: Tile[] = [
    { key: 'kAwaitingApproval', value: d.trucksAwaitingApproval, tone: flag(d.trucksAwaitingApproval, 'warn'), open: 'trucks' },
    { key: 'kReviews', value: d.openReviews, tone: flag(d.openReviews, 'warn'), open: 'review' },
    { key: 'kAlertsCritical', value: d.openAlertsCritical, tone: flag(d.openAlertsCritical, 'bad'), open: 'alerts' },
    { key: 'kAlertsOther', value: d.openAlertsOther, open: 'alerts' },
    { key: 'kCameraFailures', value: d.cameraFailures, tone: flag(d.cameraFailures, 'bad'), open: 'cameras' },
    { key: 'kRecordingFailures', value: d.recordingFailuresToday, tone: flag(d.recordingFailuresToday, 'bad'), open: 'cameras' },
    { key: 'kSyncFailures', value: d.syncFailures + d.phonesOverdue, tone: flag(d.syncFailures + d.phonesOverdue, 'warn'), open: 'alerts' },
    { key: 'kUnclosed', value: d.unclosedTrips, tone: flag(d.unclosedTrips, 'warn'), open: 'receipts' },
  ]
  const maxHour = Math.max(1, ...d.byHour.map((h) => h.count))
  const tile = (x: Tile) => {
    const body = <><div className="tile-value">{x.value}</div><div className="tile-label">{t(x.key)}</div></>
    return x.open && onOpen && (canOpen?.(x.open) ?? true)
      ? <button key={x.key} className={`tile ${x.tone ?? ''}`} onClick={() => onOpen(x.open!)}>{body}</button>
      : <div key={x.key} className={`tile ${x.tone ?? ''}`}>{body}</div>
  }

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <h2>{t('attention')}</h2>
      <div className="tiles">{attention.map(tile)}</div>
      <h2>{t('operationsToday')} — {d.day}</h2>
      <div className="tiles">{today.map(tile)}</div>
      <div className="two-cols">
        <Section title={t('byHour')}>
          <div className="bars" role="img" aria-label={t('byHour')}>
            {d.byHour.map((h) => (
              <div key={h.key} className="bar-col" title={`${h.key}h: ${h.count}`}>
                <div className="bar-value">{h.count || ''}</div>
                <div className="bar" style={{ height: `${(h.count / maxHour) * 100}%` }} />
                <div className="bar-label">{h.key}</div>
              </div>
            ))}
          </div>
        </Section>
        <Section title={t('byTruck')}>
          <table><tbody>{d.byTruck.map((x) => <tr key={x.key}><td>{x.label ?? x.key}</td><td className="num"><strong>{x.count}</strong></td></tr>)}</tbody></table>
        </Section>
        <Section title={t('byDriver')}>
          <table><tbody>{d.byDriver.map((x) => <tr key={x.key}><td>{x.label ?? x.key}</td><td className="num"><strong>{x.count}</strong></td></tr>)}</tbody></table>
        </Section>
      </div>
      <section className="card flush">
        <div className="card-head"><h2>{t('recent')}</h2></div>
        <table>
          <thead><tr><th>{t('time')}</th><th>{t('direction')}</th><th>{t('truck')}</th><th>{t('ticket')}</th><th>{t('outcome')}</th><th /></tr></thead>
          <tbody>
            {d.recent.map((r) => (
              <tr key={r.scanId}>
                <td>{fmtTime(r.scannedAt, lang)}</td><td>{t(`dir${r.direction}` as Key)}</td><td>{r.fleetNumber ?? '—'}</td>
                <td className="code">{r.ticketId ?? '—'}</td>
                <td><span className={`badge ${r.outcome.toLowerCase()}`}>{t(`outcome${r.outcome}` as Key)}</span></td>
                <td className="small">{r.reasons.map((x) => reason(x)).join(' · ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  )
}
