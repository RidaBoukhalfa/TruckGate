import { useCallback, useEffect, useState } from 'react'
import { api, type PaymentBatch, type Role, type Statement } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

const today = () => new Date().toISOString().slice(0, 10)
const firstOfMonth = () => today().slice(0, 8) + '01'

/** Accountant: payable trips per driver for a period, CSV for Excel, and recorded payments. */
export default function PayPage({ role }: { role: Role }) {
  const { t, lang } = useI18n()
  const [from, setFrom] = useState(firstOfMonth)
  const [to, setTo] = useState(today)
  const [st, setSt] = useState<Statement | null>(null)
  const [batches, setBatches] = useState<PaymentBatch[]>([])
  const [note, setNote] = useState('')
  const act = useAction()
  const canPay = role === 'ACCOUNTANT' || role === 'ADMIN'
  const money = (n: number) => n.toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { minimumFractionDigits: 2, maximumFractionDigits: 2 })

  const load = useCallback(async () => {
    setSt(await api.get<Statement>(`/api/pay/statement?from=${from}&to=${to}`))
    setBatches(await api.get<PaymentBatch[]>('/api/pay/batches'))
  }, [from, to])
  useEffect(() => { act.run(load) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const pay = () => act.run(async () => {
    if (!st || !window.confirm(t('confirmPayment', { n: st.trips.length, total: `${money(st.total)} ${st.currency}` }))) return
    await api.post('/api/pay/batches', { from, to, note: note || null })
    setNote('')
    await load()
  })

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <Section title={t('statement')}>
        <div className="row">
          <label>{t('from')}<input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
          <label>{t('to')}<input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
          <button className="primary" disabled={act.busy} onClick={() => act.run(load)}>{t('show')}</button>
          <a className="button" href={`/api/pay/statement.csv?from=${from}&to=${to}&lang=${lang}`}>{t('csv')}</a>
        </div>
        {st && (
          <>
            <p className="muted">
              {t('pricePerTrip')}: <strong>{money(st.pricePerTrip)} {st.currency}</strong> · {t('partialPay')}: {st.partialPercent}%
              {st.pricePerTrip <= 0 && <span className="warn"> — {t('priceNotSet')}</span>}
            </p>
            <table>
              <thead><tr><th>{t('driver')}</th><th>{t('trips')}</th><th>{t('full')}</th><th>{t('partial')}</th><th>{t('amount')}</th></tr></thead>
              <tbody>
                {st.lines.map((l) => (
                  <tr key={l.driverId}>
                    <td>{l.driverName} <span className="muted small">{l.employmentType ? t((l.employmentType === 'CONTRACTOR' ? 'contractor' : 'employee') as Key) : ''}</span></td>
                    <td>{l.trips}</td><td>{l.full}</td><td>{l.partial}</td><td><strong>{money(l.amount)}</strong></td>
                  </tr>
                ))}
                <tr className="total"><td>{t('total')}</td><td>{st.trips.length}</td><td /><td /><td><strong>{money(st.total)} {st.currency}</strong></td></tr>
              </tbody>
            </table>
            {st.notYetPayable > 0 && <p className="warn">{t('notYetPayable', { n: st.notYetPayable })}</p>}
            {canPay && (
              <div className="row">
                <label>{t('note')}<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
                <button className="danger" disabled={act.busy || st.trips.length === 0 || st.pricePerTrip <= 0} onClick={pay}>{t('recordPayment')}</button>
              </div>
            )}
          </>
        )}
      </Section>
      <Section title={t('batches')}>
        <table>
          <tbody>
            {batches.map((b) => (
              <tr key={b.id}>
                <td>#{b.id}</td><td>{b.periodFrom} → {b.periodTo}</td><td>{b.tripCount} {t('trips')}</td>
                <td><strong>{money(b.total)} {b.currency}</strong></td><td className="small muted">{b.note}</td>
                <td className="small muted">{b.createdByName} · {fmtTime(b.createdAt, lang)}</td>
                <td><a href={`/api/pay/batches/${b.id}.csv?lang=${lang}`}>CSV</a></td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </div>
  )
}
