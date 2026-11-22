import { useCallback, useEffect, useState } from 'react'
import { api, type Dashboard } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

/** Manager: today at a glance, refreshed every 30 seconds. */
export default function DashboardPage() {
  const { t, reason, lang } = useI18n()
  const [d, setD] = useState<Dashboard | null>(null)
  const act = useAction()

  const load = useCallback(async () => setD(await api.get<Dashboard>('/api/reports/dashboard')), [])
  useEffect(() => {
    act.run(load)
    const timer = setInterval(() => { load().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  if (!d) return <ErrorBox error={act.error} />
  const tiles: [Key, number, string?][] = [
    ['kEntries', d.entries], ['kExits', d.exits], ['kAccepted', d.accepted, 'ok'], ['kPending', d.pending, d.pending ? 'warn' : ''],
    ['kRejected', d.rejected, d.rejected ? 'bad' : ''], ['kPartial', d.partial], ['kInside', d.inside],
    ['kReviews', d.openReviews, d.openReviews ? 'warn' : ''], ['kAlertsCritical', d.openAlertsCritical, d.openAlertsCritical ? 'bad' : ''],
    ['kAlertsOther', d.openAlertsOther], ['kPhonesOverdue', d.phonesOverdue, d.phonesOverdue ? 'warn' : ''],
  ]
  const maxHour = Math.max(1, ...d.byHour.map((h) => h.count))

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <div className="tiles">
        {tiles.map(([k, v, cls]) => (
          <div key={k} className={`tile ${cls ?? ''}`}><div className="tile-value">{v}</div><div className="tile-label">{t(k)}</div></div>
        ))}
      </div>
      <div className="two-cols">
        <Section title={`${t('byHour')} — ${d.day}`}>
          <div className="bars">
            {d.byHour.map((h) => (
              <div key={h.key} className="bar-col" title={`${h.key}h: ${h.count}`}>
                <div className="bar" style={{ height: `${(h.count / maxHour) * 100}%` }} />
                <div className="bar-label">{h.key}</div>
                <div className="bar-value">{h.count}</div>
              </div>
            ))}
          </div>
        </Section>
        <Section title={t('byTruck')}>
          <table><tbody>{d.byTruck.map((x) => <tr key={x.key}><td>{x.label ?? x.key}</td><td><strong>{x.count}</strong></td></tr>)}</tbody></table>
        </Section>
        <Section title={t('byDriver')}>
          <table><tbody>{d.byDriver.map((x) => <tr key={x.key}><td>{x.label ?? x.key}</td><td><strong>{x.count}</strong></td></tr>)}</tbody></table>
        </Section>
      </div>
      <Section title={t('recent')}>
        <table>
          <thead><tr><th>{t('time')}</th><th>{t('direction')}</th><th>{t('truck')}</th><th>{t('ticket')}</th><th>{t('outcome')}</th><th /></tr></thead>
          <tbody>
            {d.recent.map((r) => (
              <tr key={r.scanId}>
                <td>{fmtTime(r.scannedAt, lang)}</td><td>{t(`dir${r.direction}` as Key)}</td><td>{r.fleetNumber ?? '—'}</td>
                <td>{r.ticketId ?? '—'}</td><td><span className={`badge ${r.outcome.toLowerCase()}`}>{r.outcome}</span></td>
                <td className="small">{r.reasons.map((x) => reason(x)).join(' · ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </div>
  )
}
