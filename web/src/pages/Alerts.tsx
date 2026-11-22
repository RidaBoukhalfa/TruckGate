import { useCallback, useEffect, useState } from 'react'
import { api, type AlertItem } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

function detail(params: string): string {
  try {
    const p = JSON.parse(params) as Record<string, unknown>
    return ['ticket_id', 'trip_number', 'label', 'error', 'driver_id', 'operator_id', 'count']
      .filter((k) => p[k] !== undefined && p[k] !== null).map((k) => `${k === 'count' ? '×' : ''}${String(p[k])}`).join(' · ')
  } catch {
    return ''
  }
}

/** Alerts raised by the gate, the phones, the cameras and the watchdog; acknowledged here once handled. */
export default function AlertsPage() {
  const { t, alertTitle, lang } = useI18n()
  const [openOnly, setOpenOnly] = useState(true)
  const [items, setItems] = useState<AlertItem[]>([])
  const act = useAction()

  const load = useCallback(async () => setItems(await api.get<AlertItem[]>(`/api/alerts?open=${openOnly}&limit=300`)), [openOnly])
  useEffect(() => {
    act.run(load)
    const timer = setInterval(() => { load().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  const ack = (a: AlertItem) => act.run(async () => { await api.post(`/api/alerts/${a.id}/ack`); await load() })

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <Section title={t('tabAlerts')} actions={
        <label className="inline small"><input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} /> {t('openOnly')}</label>
      }>
        {items.length === 0 && <p className="muted">{t('noAlerts')}</p>}
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
                  {a.failedDeliveries > 0 && <span className="warn">{t('failedN', { n: a.failedDeliveries })}</span>}
                </td>
                <td>{a.ackAt
                  ? <span className="small muted">{t('ackBy')} {a.ackByName} · {fmtTime(a.ackAt, lang)}</span>
                  : <button disabled={act.busy} onClick={() => ack(a)}>{t('ack')}</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </div>
  )
}
