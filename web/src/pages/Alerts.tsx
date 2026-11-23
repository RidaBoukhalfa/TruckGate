import { useCallback, useEffect, useState } from 'react'
import { api, type ActivityItem, type AlertItem } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, fmtTime, useAction } from '../ui'

function detail(params: string): string {
  try {
    const p = JSON.parse(params) as Record<string, unknown>
    return ['ticket_id', 'trip_number', 'fleet_number', 'plate', 'operator', 'label', 'camera', 'error', 'driver_id', 'operator_id', 'count']
      .filter((k) => p[k] !== undefined && p[k] !== null && p[k] !== '').map((k) => `${k === 'count' ? '×' : ''}${String(p[k])}`).join(' · ')
  } catch {
    return ''
  }
}

const activityKey = (a: ActivityItem): Key => {
  if (a.kind === 'TRUCK') return `truck${a.code}` as Key
  return `act${a.kind}` as Key
}

/**
 * Notification centre: alerts raised by the gate, the phones, the cameras, the recorder and the watchdog
 * (acknowledged here once handled), and the activity log of the last hours.
 */
export default function AlertsPage({ onChanged }: { onChanged?: () => void }) {
  const { t, alertTitle, reason, lang } = useI18n()
  const [view, setView] = useState<'alerts' | 'activity'>('alerts')
  const [openOnly, setOpenOnly] = useState(true)
  const [items, setItems] = useState<AlertItem[] | null>(null)
  const [activity, setActivity] = useState<ActivityItem[] | null>(null)
  const [hours, setHours] = useState(24)
  const act = useAction()

  const load = useCallback(async () => {
    if (view === 'alerts') setItems(await api.get<AlertItem[]>(`/api/alerts?open=${openOnly}&limit=300`))
    else setActivity(await api.get<ActivityItem[]>(`/api/activity?hours=${hours}&limit=300`))
  }, [view, openOnly, hours])
  useEffect(() => {
    act.run(load)
    const timer = setInterval(() => { load().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  const ack = (a: AlertItem) => act.run(async () => { await api.post(`/api/alerts/${a.id}/ack`); await load(); onChanged?.() })
  const openCount = items?.filter((a) => !a.ackAt).length ?? 0

  return (
    <div className="stack">
      <div className="tabs">
        <button className={view === 'alerts' ? 'active' : ''} onClick={() => setView('alerts')}>
          {t('alertsTab')}{openCount ? <span className="nav-count">{openCount}</span> : null}
        </button>
        <button className={view === 'activity' ? 'active' : ''} onClick={() => setView('activity')}>{t('activity')}</button>
      </div>
      <ErrorBox error={act.error} />

      {view === 'alerts' && (
        <section className="card flush">
          <div className="filters">
            <label className="inline small"><input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} /> {t('openOnly')}</label>
          </div>
          {items === null && <div className="loading">{t('loading')}</div>}
          {items?.length === 0 && <div className="empty"><strong>{t('noAlerts')}</strong></div>}
          {items && items.length > 0 && (
            <table>
              <tbody>
                {items.map((a) => (
                  <tr key={a.id} className={a.ackAt ? 'inactive' : ''}>
                    <td><span className={`badge sev-${a.severity.toLowerCase()}`}>{t(`sev${a.severity}` as Key)}</span></td>
                    <td><strong>{alertTitle(a.type)}</strong><div className="small muted">{detail(a.params)}</div></td>
                    <td className="small">{fmtTime(a.createdAt, lang)}</td>
                    <td className="small">
                      {a.smsSentAt && <span title={fmtTime(a.smsSentAt, lang)}>SMS ✓ </span>}
                      {a.telegramSentAt && <span title={fmtTime(a.telegramSentAt, lang)}>Telegram ✓ </span>}
                      {a.whatsappSentAt && <span title={fmtTime(a.whatsappSentAt, lang)}>WhatsApp ✓ </span>}
                      {a.failedDeliveries > 0 && <span className="warn">{t('failedN', { n: a.failedDeliveries })}</span>}
                    </td>
                    <td>{a.ackAt
                      ? <span className="small muted">{t('ackBy')} {a.ackByName} · {fmtTime(a.ackAt, lang)}</span>
                      : <button disabled={act.busy} onClick={() => ack(a)}>{t('ack')}</button>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      )}

      {view === 'activity' && (
        <section className="card flush">
          <div className="filters">
            <label>{t('time')}
              <select value={hours} onChange={(e) => setHours(Number(e.target.value))}>
                <option value={6}>6 h</option><option value={24}>{t('last24h')}</option><option value={72}>72 h</option><option value={168}>7 j</option>
              </select>
            </label>
          </div>
          {activity === null && <div className="loading">{t('loading')}</div>}
          {activity?.length === 0 && <div className="empty">{t('noActivity')}</div>}
          {activity && activity.length > 0 && (
            <table>
              <tbody>
                {activity.map((a, i) => (
                  <tr key={i}>
                    <td className="small">{fmtTime(a.at, lang)}</td>
                    <td><span className="badge">{t(activityKey(a))}</span></td>
                    <td>{a.kind === 'TRUCK' ? '' : a.kind === 'TRIP' ? t(`state${a.code}` as Key) : t(`outcome${a.code}` as Key)}</td>
                    <td>{a.fleetNumber ?? ''} <span className="code small muted">{a.tripNumber ?? ''}</span></td>
                    <td className="small muted">{a.kind === 'TRUCK' ? (a.detail ?? '')
                      : (a.detail ?? '').split(',').filter(Boolean).map((x) => reason(x)).join(' · ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      )}
    </div>
  )
}
