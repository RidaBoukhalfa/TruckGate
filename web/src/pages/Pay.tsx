import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, type Adjustment, type PaymentBatch, type Role, type Statement } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

const today = () => new Date().toISOString().slice(0, 10)
const firstOfMonth = () => today().slice(0, 8) + '01'

function useMoney() {
  const { lang } = useI18n()
  return (n: number | null) => (n === null ? '—'
    : n.toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { minimumFractionDigits: 2, maximumFractionDigits: 2 }))
}

/** The per-truck statement table (also used for a recorded payment). */
function TruckTable({ st, selected, onToggle }: {
  st: Statement; selected?: Set<number>; onToggle?: (id: number, on: boolean) => void
}) {
  const { t } = useI18n()
  const money = useMoney()
  const sum = (f: (x: Statement['trucks'][number]) => number) => st.trucks.reduce((a, x) => a + f(x), 0)
  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            {onToggle && <th className="no-print" />}
            <th>{t('truckNumber')}</th><th>{t('ownerLastName')} {t('ownerFirstName')}</th><th>{t('matricule')}</th>
            <th className="num">{t('eligibleTrips')}</th><th className="num">{t('full')} / {t('partial')}</th>
            <th className="num">{t('price')}</th><th className="num">{t('tripsAmount')}</th><th className="num">{t('adjustments')}</th>
            <th className="num">{t('total')}</th><th className="num">{t('paidInPeriod')}</th><th>{t('paymentStatus')}</th>
          </tr>
        </thead>
        <tbody>
          {st.trucks.map((x) => (
            <tr key={x.truckId}>
              {onToggle && (
                <td className="no-print">
                  <input type="checkbox" disabled={x.total === 0 && x.trips === 0} checked={selected?.has(x.truckId) ?? false}
                    onChange={(e) => onToggle(x.truckId, e.target.checked)} aria-label={x.fleetNumber ?? String(x.truckId)} />
                </td>
              )}
              <td><strong>{x.fleetNumber ?? x.truckId}</strong></td>
              <td>{x.ownerLastName ?? ''} {x.ownerFirstName ?? ''}</td>
              <td className="code">{x.plate ?? '—'}</td>
              <td className="num">{x.trips}</td>
              <td className="num">{x.full} / {x.partial}</td>
              <td className="num">{money(x.pricePerTrip)}</td>
              <td className="num">{money(x.tripsAmount)}</td>
              <td className={`num ${x.adjustments < 0 ? 'bad' : ''}`}>{x.adjustments ? money(x.adjustments) : ''}</td>
              <td className="num"><strong>{money(x.total)}</strong></td>
              <td className="num small">{x.paidTrips ? `${x.paidTrips} · ${money(x.paidAmount)}` : ''}</td>
              <td><span className={`badge ${x.paymentStatus === 'PAID' ? 'ok' : x.paymentStatus === 'UNPAID' ? 'warn' : ''}`}>
                {t(`ps${x.paymentStatus}` as Key)}</span></td>
            </tr>
          ))}
          <tr className="total">
            {onToggle && <td className="no-print" />}
            <td colSpan={3}>{t('total')}</td>
            <td className="num">{sum((x) => x.trips)}</td><td />
            <td /><td className="num">{money(st.tripsTotal)}</td><td className="num">{money(st.adjustmentsTotal)}</td>
            <td className="num"><strong>{money(st.total)} {st.currency}</strong></td><td /><td />
          </tr>
        </tbody>
      </table>
    </div>
  )
}

function PrintHeader({ title, st }: { title: string; st: Statement }) {
  const { t, lang } = useI18n()
  return (
    <div className="print-only print-head">
      <h1>TruckGate — {title}</h1>
      <div>{t('from')} {st.from} {t('to')} {st.to} · {t('partialPay')}: {st.partialPercent}% · {t('printedOn')} {fmtTime(new Date().toISOString(), lang)}</div>
      <div>{t('formula')}</div>
    </div>
  )
}

/**
 * Accountant: per-truck statement (Total = eligible trips × the truck's approved price + adjustments),
 * trips that cannot be paid yet and why, adjustments, payments by truck, CSV and print / PDF.
 */
export default function PayPage({ role }: { role: Role }) {
  const { t, lang } = useI18n()
  const money = useMoney()
  const [from, setFrom] = useState(firstOfMonth)
  const [to, setTo] = useState(today)
  const [st, setSt] = useState<Statement | null>(null)
  const [batches, setBatches] = useState<PaymentBatch[]>([])
  const [pending, setPending] = useState<Adjustment[]>([])
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [note, setNote] = useState('')
  const [adj, setAdj] = useState({ truckId: '', tripId: '', amount: '', reason: '' })
  const [batch, setBatch] = useState<{ id: number; st: Statement } | null>(null)
  const [tab, setTab] = useState<'statement' | 'excluded' | 'adjustments' | 'drivers' | 'batches'>('statement')
  const act = useAction()
  const canPay = role === 'ACCOUNTANT' || role === 'ADMIN'

  const load = useCallback(async () => {
    const s = await api.get<Statement>(`/api/pay/statement?from=${from}&to=${to}`)
    setSt(s)
    setSelected(new Set(s.trucks.filter((x) => x.total !== 0 || x.trips > 0).map((x) => x.truckId)))
    setBatches(await api.get<PaymentBatch[]>('/api/pay/batches'))
    setPending(await api.get<Adjustment[]>('/api/pay/adjustments?pending=true'))
  }, [from, to])
  useEffect(() => { act.run(load) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const chosen = useMemo(() => (st ? st.trucks.filter((x) => selected.has(x.truckId)) : []), [st, selected])
  const chosenTotal = chosen.reduce((a, x) => a + x.total, 0)

  const pay = () => act.run(async () => {
    if (!st || !chosen.length) return
    if (!window.confirm(t('confirmPayTrucks', { n: chosen.length, total: `${money(chosenTotal)} ${st.currency}` }))) return
    await api.post('/api/pay/batches/by-truck', { from, to, note: note.trim() || null, truckIds: chosen.map((x) => x.truckId) })
    setNote('')
    await load()
  })
  const addAdjustment = () => act.run(async () => {
    await api.post('/api/pay/adjustments', {
      truckId: Number(adj.truckId), tripId: adj.tripId ? Number(adj.tripId) : null, amount: Number(adj.amount), reason: adj.reason.trim(),
    })
    setAdj({ truckId: '', tripId: '', amount: '', reason: '' })
    await load()
  })
  const openBatch = (id: number) => act.run(async () => setBatch({ id, st: await api.get<Statement>(`/api/pay/batches/${id}`) }))
  const toggle = (id: number, on: boolean) => setSelected((s) => { const n = new Set(s); if (on) n.add(id); else n.delete(id); return n })

  const tabs = [
    ['statement', 'byTruckStatement', 0], ['excluded', 'excluded', st?.excluded.length ?? 0],
    ['adjustments', 'adjustments', pending.length], ['drivers', 'byDriver', 0], ['batches', 'batches', 0],
  ] as const

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <section className="card flush">
        <form className="filters no-print" onSubmit={(e) => { e.preventDefault(); act.run(load) }}>
          <label>{t('from')}<input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
          <label>{t('to')}<input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
          <button className="primary" disabled={act.busy}>{t('show')}</button>
          <a className="button" href={`/api/pay/statement.csv?from=${from}&to=${to}&lang=${lang}`}>{t('csv')}</a>
          <button type="button" onClick={() => window.print()} disabled={!st}>{t('printPdf')}</button>
          <span className="spacer" />
          <span className="muted small">{t('formula')}</span>
        </form>
        <div className="tabs no-print" style={{ padding: '0 16px' }}>
          {tabs.map(([id, label, n]) => (
            <button key={id} className={tab === id ? 'active' : ''} onClick={() => setTab(id)}>
              {t(label)}{n ? <span className="nav-count">{n}</span> : null}
            </button>
          ))}
        </div>
        {!st && <div className="loading">{t('loading')}</div>}

        {st && tab === 'statement' && (
          <>
            <PrintHeader title={t('byTruckStatement')} st={st} />
            {st.trucks.length === 0 ? <div className="empty">{t('none')}</div>
              : <TruckTable st={st} selected={canPay ? selected : undefined} onToggle={canPay ? toggle : undefined} />}
            {st.notYetPayable > 0 && <p className="warn small" style={{ padding: '0 16px' }}>{t('notYetPayable', { n: st.notYetPayable })}</p>}
            {canPay && (
              <div className="filters no-print">
                <label className="grow">{t('note')}<input value={note} onChange={(e) => setNote(e.target.value)} /></label>
                <strong>{chosen.length} · {money(chosenTotal)} {st.currency}</strong>
                <button className="danger" disabled={act.busy || chosen.length === 0} onClick={pay}>{t('paySelected')}</button>
              </div>
            )}
            <div className="print-only print-sign">{t('signature')}: ______________________</div>
          </>
        )}

        {st && tab === 'excluded' && (
          st.excluded.length === 0 ? <div className="empty">{t('none')}</div> : (
            <table>
              <thead><tr><th>{t('trip')}</th><th>{t('truck')}</th><th>{t('entered')}</th><th>{t('statusLabel')}</th><th>{t('reviewStatus')}</th></tr></thead>
              <tbody>{st.excluded.map((x) => (
                <tr key={x.tripId}>
                  <td className="code">{x.tripNumber}</td><td>{x.fleetNumber ?? x.truckId}</td><td className="small">{fmtTime(x.enteredAt, lang)}</td>
                  <td>{t(`state${x.state}` as Key)}</td>
                  <td><span className="badge warn">{t(`ex${x.reason}` as Key)}</span></td>
                </tr>
              ))}</tbody>
            </table>
          )
        )}

        {st && tab === 'adjustments' && (
          <div className="stack" style={{ padding: 16 }}>
            {canPay && (
              <div>
                <h3>{t('newAdjustment')}</h3>
                <p className="muted small">{t('adjustmentHelp')}</p>
                <div className="row">
                  <label>{t('truck')} *
                    <select value={adj.truckId} onChange={(e) => setAdj({ ...adj, truckId: e.target.value })}>
                      <option value="">—</option>
                      {st.trucks.map((x) => <option key={x.truckId} value={x.truckId}>{x.fleetNumber ?? x.truckId} · {x.ownerLastName ?? ''}</option>)}
                    </select>
                  </label>
                  <label>#<input type="number" min={1} className="narrow" value={adj.truckId} onChange={(e) => setAdj({ ...adj, truckId: e.target.value })} /></label>
                  <label>{t('trip')} (id)<input type="number" min={1} className="narrow" value={adj.tripId} onChange={(e) => setAdj({ ...adj, tripId: e.target.value })} /></label>
                  <label>{t('amount')} *<input type="number" step="0.01" className="narrow-wide" value={adj.amount} onChange={(e) => setAdj({ ...adj, amount: e.target.value })} /></label>
                  <label className="grow">{t('decisionReason')} *<input value={adj.reason} onChange={(e) => setAdj({ ...adj, reason: e.target.value })} /></label>
                  <button className="primary" disabled={act.busy || !adj.truckId || !Number(adj.amount) || !adj.reason.trim()} onClick={addAdjustment}>{t('add')}</button>
                </div>
              </div>
            )}
            <h3>{t('pendingAdjustments')}</h3>
            {pending.length === 0 ? <div className="muted">{t('none')}</div> : (
              <table>
                <tbody>{pending.map((a) => (
                  <tr key={a.id}>
                    <td>{a.fleetNumber ?? a.truckId}</td><td className="code">{a.tripNumber ?? ''}</td>
                    <td className={`num ${a.amount < 0 ? 'bad' : 'ok'}`}><strong>{money(a.amount)}</strong></td>
                    <td>{a.reason}</td><td className="small muted">{a.createdByName} · {fmtTime(a.createdAt, lang)}</td>
                  </tr>
                ))}</tbody>
              </table>
            )}
          </div>
        )}

        {st && tab === 'drivers' && (
          <table>
            <thead><tr><th>{t('driver')}</th><th className="num">{t('trips')}</th><th className="num">{t('full')}</th><th className="num">{t('partial')}</th><th className="num">{t('amount')}</th></tr></thead>
            <tbody>
              {st.lines.map((l) => (
                <tr key={l.driverId}>
                  <td>{l.driverName} <span className="muted small">{t(l.employmentType === 'CONTRACTOR' ? 'contractor' : 'employee')}</span></td>
                  <td className="num">{l.trips}</td><td className="num">{l.full}</td><td className="num">{l.partial}</td><td className="num">{money(l.amount)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {tab === 'batches' && (
          <table>
            <thead><tr><th>#</th><th>{t('from')} → {t('to')}</th><th>{t('perTruck')}</th><th className="num">{t('trips')}</th>
              <th className="num">{t('adjustments')}</th><th className="num">{t('total')}</th><th>{t('note')}</th><th /><th /></tr></thead>
            <tbody>
              {batches.map((b) => (
                <tr key={b.id}>
                  <td>#{b.id}</td><td className="small">{b.periodFrom} → {b.periodTo}</td>
                  <td className="small">{b.pricing === 'PER_TRUCK' ? t('perTruck') : `${t('globalPrice')} · ${money(b.pricePerTrip)}`}</td>
                  <td className="num">{b.tripCount}</td><td className="num">{b.adjustmentsTotal ? money(b.adjustmentsTotal) : ''}</td>
                  <td className="num"><strong>{money(b.total)} {b.currency}</strong></td><td className="small muted">{b.note}</td>
                  <td className="small muted">{b.createdByName} · {fmtTime(b.createdAt, lang)}</td>
                  <td className="no-print">
                    <button className="link" onClick={() => openBatch(b.id)}>{t('batchDetail')}</button>
                    <a href={`/api/pay/batches/${b.id}.csv?lang=${lang}`}>CSV</a>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {batch && (
        <>
          <div className="drawer-back no-print" onClick={() => setBatch(null)} />
          <aside className="drawer print-drawer" role="dialog">
            <div className="drawer-head no-print">
              <h2>{t('batchDetail')} #{batch.id}</h2>
              <div className="row-between">
                <button onClick={() => window.print()}>{t('printPdf')}</button>
                <a className="button" href={`/api/pay/batches/${batch.id}.csv?lang=${lang}`}>{t('csv')}</a>
                <button onClick={() => setBatch(null)}>{t('close2')}</button>
              </div>
            </div>
            <Section title={`#${batch.id} · ${batch.st.from} → ${batch.st.to}`}>
              <PrintHeader title={`${t('batchDetail')} #${batch.id}`} st={batch.st} />
              <TruckTable st={batch.st} />
              {batch.st.adjustments.length > 0 && (
                <>
                  <h3>{t('adjustments')}</h3>
                  <table><tbody>{batch.st.adjustments.map((a) => (
                    <tr key={a.id}><td>{a.fleetNumber ?? a.truckId}</td><td className="num">{money(a.amount)}</td><td>{a.reason}</td></tr>
                  ))}</tbody></table>
                </>
              )}
              <h3>{t('trips')}</h3>
              <table>
                <tbody>{batch.st.trips.map((x) => (
                  <tr key={x.tripId}><td className="code">{x.tripNumber}</td><td className="small">{fmtTime(x.enteredAt, lang)}</td>
                    <td>{x.fleetNumber ?? x.truckId}</td><td>{x.driverName}</td><td>{x.loadStatus ? t(`d${x.loadStatus}` as Key) : ''}</td>
                    <td className="num">{money(x.amount)}</td></tr>
                ))}</tbody>
              </table>
              <div className="print-only print-sign">{t('signature')}: ______________________</div>
            </Section>
          </aside>
        </>
      )}
    </div>
  )
}
