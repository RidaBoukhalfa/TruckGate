import { useCallback, useEffect, useState } from 'react'
import { api, type Driver, type FleetTruck, type SettingItem, type TruckHistory, type TruckStatus } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

type View = 'pending' | 'registered' | 'drivers'
interface Decision { truckId: number; status: string; reviewsResolved: number }
interface Form { fleetNumber: string; ownerLastName: string; ownerFirstName: string; plate: string; price: string }

const statusTone: Record<TruckStatus, string> = { PENDING_APPROVAL: 'amber', APPROVED: 'ok', REJECTED: 'bad', MERGED: '' }

function useMoney() {
  const { lang } = useI18n()
  return (n: number | null) => (n === null ? '—'
    : n.toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { minimumFractionDigits: 2, maximumFractionDigits: 2 }))
}

/** One truck added on a quarry phone: complete the five fields and approve, reject with a reason, or merge. */
function PendingCard({ truck, approved, defaultPrice, onDone }: {
  truck: FleetTruck; approved: FleetTruck[]; defaultPrice: string; onDone: (d: Decision) => void
}) {
  const { t, lang } = useI18n()
  const [f, setF] = useState<Form>({
    fleetNumber: truck.fleetNumber === `P${truck.id}` ? '' : truck.fleetNumber, ownerLastName: truck.ownerLastName ?? '',
    ownerFirstName: truck.ownerFirstName ?? '', plate: truck.reportedPlate ?? truck.plate ?? '', price: defaultPrice,
  })
  const [mode, setMode] = useState<'approve' | 'reject' | 'merge'>('approve')
  const [why, setWhy] = useState('')
  const [target, setTarget] = useState<number | ''>(truck.possibleDuplicateOf ?? '')
  const act = useAction()
  const set = (k: keyof Form, v: string) => setF((x) => ({ ...x, [k]: v }))
  useEffect(() => { if (defaultPrice) setF((x) => (x.price ? x : { ...x, price: defaultPrice })) }, [defaultPrice])
  const complete = f.fleetNumber.trim() && f.ownerLastName.trim() && f.ownerFirstName.trim() && f.plate.trim() && Number(f.price) > 0

  const approve = () => act.run(async () => onDone(await api.post<Decision>(`/api/fleet/trucks/${truck.id}/approve`, {
    fleetNumber: f.fleetNumber.trim(), ownerLastName: f.ownerLastName.trim(), ownerFirstName: f.ownerFirstName.trim(),
    plate: f.plate.trim(), price: Number(f.price), model: truck.model, defaultDriverId: truck.defaultDriverId,
  })))
  const reject = () => act.run(async () => onDone(await api.post<Decision>(`/api/fleet/trucks/${truck.id}/reject`, { reason: why.trim() })))
  const merge = () => act.run(async () => onDone(await api.post<Decision>(`/api/fleet/trucks/${truck.id}/merge`,
    { targetId: Number(target), reason: why.trim() || null })))

  return (
    <div className="card pending-truck">
      <div className="card-head">
        <h2>#{truck.id} · <span className="code">{truck.reportedPlate ?? '—'}</span></h2>
        <span className="badge amber">{t('tsPENDING_APPROVAL')}</span>
      </div>
      <dl className="facts small">
        <dt>{t('createdBy')}</dt>
        <dd>{truck.createdByName ?? '—'}{truck.createdByDeviceLabel ? ` · ${truck.createdByDeviceLabel}` : ''} · {fmtTime(truck.createdAt, lang)}</dd>
        <dt>{t('reportedPlate')}</dt><dd className="code">{truck.reportedPlate ?? '—'}</dd>
        <dt>{t('observedAtGate')}</dt><dd className="code">{truck.observedPlates.length ? truck.observedPlates.join(' · ') : '—'}</dd>
        <dt>{t('tripsCount')}</dt><dd>{truck.tripCount}{truck.lastTripAt ? ` · ${t('lastTrip')} ${fmtTime(truck.lastTripAt, lang)}` : ''}</dd>
      </dl>
      {truck.possibleDuplicateOf && (
        <div className="banner pending small">
          {t('possibleDuplicate')}: <strong>{truck.duplicateFleetNumber}</strong> <span className="code">{truck.duplicatePlate}</span>
          {' '}<button className="link" onClick={() => { setMode('merge'); setTarget(truck.possibleDuplicateOf!) }}>{t('mergeInto')}</button>
        </div>
      )}
      <div className="tabs">
        {(['approve', 'reject', 'merge'] as const).map((m) => (
          <button key={m} className={mode === m ? 'active' : ''} onClick={() => setMode(m)}>
            {t(m === 'approve' ? 'approve' : m === 'reject' ? 'rejectTruck' : 'mergeInto')}
          </button>
        ))}
      </div>
      <ErrorBox error={act.error} />
      {mode === 'approve' && (
        <>
          <p className="muted small">{t('fiveFields')}</p>
          <div className="form-grid">
            <label>{t('truckNumber')} *<input value={f.fleetNumber} onChange={(e) => set('fleetNumber', e.target.value)} /></label>
            <label>{t('ownerLastName')} *<input value={f.ownerLastName} onChange={(e) => set('ownerLastName', e.target.value)} /></label>
            <label>{t('ownerFirstName')} *<input value={f.ownerFirstName} onChange={(e) => set('ownerFirstName', e.target.value)} /></label>
            <label>{t('matricule')} *<input className="code" inputMode="numeric" value={f.plate} onChange={(e) => set('plate', e.target.value)} /></label>
            <label>{t('tripPrice')} *<input type="number" min={0} step="0.01" value={f.price} onChange={(e) => set('price', e.target.value)} /></label>
          </div>
          <div className="row"><button className="primary" disabled={act.busy || !complete} onClick={approve}>{t('approve')}</button></div>
        </>
      )}
      {mode === 'reject' && (
        <div className="row">
          <label className="grow">{t('rejectReason')} *<input value={why} onChange={(e) => setWhy(e.target.value)} /></label>
          <button className="danger" disabled={act.busy || !why.trim()} onClick={reject}>{t('rejectTruck')}</button>
        </div>
      )}
      {mode === 'merge' && (
        <div className="row">
          <label>{t('mergeInto')} *
            <select value={target} onChange={(e) => setTarget(e.target.value ? Number(e.target.value) : '')}>
              <option value="">—</option>
              {approved.map((a) => <option key={a.id} value={a.id}>{a.fleetNumber} · {a.plate ?? '—'} · {a.ownerLastName ?? ''} {a.ownerFirstName ?? ''}</option>)}
            </select>
          </label>
          <label className="grow">{t('mergeReason')}<input value={why} onChange={(e) => setWhy(e.target.value)} /></label>
          <button className="primary" disabled={act.busy || target === ''} onClick={merge}>{t('mergeInto')}</button>
        </div>
      )}
    </div>
  )
}

/** Detail drawer of a registered truck: edit, plate, price (with reason), activation and full history. */
function TruckDrawer({ id, drivers, onClose, onChanged }: { id: number; drivers: Driver[]; onClose: () => void; onChanged: () => void }) {
  const { t, lang, reason } = useI18n()
  const money = useMoney()
  const [h, setH] = useState<TruckHistory | null>(null)
  const [edit, setEdit] = useState({ fleetNumber: '', ownerLastName: '', ownerFirstName: '', model: '', defaultDriverId: '' })
  const [plate, setPlate] = useState('')
  const [price, setPrice] = useState({ value: '', reason: '' })
  const act = useAction()

  const load = useCallback(async () => {
    const x = await api.get<TruckHistory>(`/api/fleet/trucks/${id}/history`)
    setH(x)
    setEdit({
      fleetNumber: x.truck.fleetNumber, ownerLastName: x.truck.ownerLastName ?? '', ownerFirstName: x.truck.ownerFirstName ?? '',
      model: x.truck.model ?? '', defaultDriverId: x.truck.defaultDriverId ? String(x.truck.defaultDriverId) : '',
    })
  }, [id])
  useEffect(() => { act.run(load) }, [load]) // eslint-disable-line react-hooks/exhaustive-deps
  const after = async () => { await load(); onChanged() }

  const saveEdit = () => act.run(async () => {
    await api.put(`/api/fleet/trucks/${id}`, {
      fleetNumber: edit.fleetNumber.trim(), ownerLastName: edit.ownerLastName || null, ownerFirstName: edit.ownerFirstName || null,
      model: edit.model || null, defaultDriverId: edit.defaultDriverId ? Number(edit.defaultDriverId) : null,
    })
    await after()
  })
  const savePlate = () => act.run(async () => { await api.put(`/api/fleet/trucks/${id}/plate`, { plate }); setPlate(''); await after() })
  const savePrice = () => act.run(async () => {
    await api.put(`/api/fleet/trucks/${id}/price`, { price: Number(price.value), reason: price.reason.trim() })
    setPrice({ value: '', reason: '' })
    await after()
  })
  const toggle = () => act.run(async () => { await api.put(`/api/fleet/trucks/${id}/active`, { active: !h?.truck.active }); await after() })

  const tr = h?.truck
  const closed = tr?.approvalStatus === 'REJECTED' || tr?.approvalStatus === 'MERGED'
  return (
    <>
      <div className="drawer-back" onClick={onClose} />
      <aside className="drawer" role="dialog">
        <div className="drawer-head">
          <h2>{tr ? `${tr.fleetNumber} · #${tr.id}` : '…'}</h2>
          <button onClick={onClose}>{t('close2')}</button>
        </div>
        <ErrorBox error={act.error} />
        {!h || !tr ? <div className="loading">{t('loading')}</div> : (
          <div className="stack">
            <Section title={t('editTruck')} actions={<span className={`badge ${statusTone[tr.approvalStatus]}`}>{t(`ts${tr.approvalStatus}` as Key)}</span>}>
              {tr.decisionReason && <p className="muted small">{tr.decidedByName} · {fmtTime(tr.decidedAt, lang)} · {tr.decisionReason}</p>}
              <div className="form-grid">
                <label>{t('truckNumber')}<input value={edit.fleetNumber} disabled={closed} onChange={(e) => setEdit({ ...edit, fleetNumber: e.target.value })} /></label>
                <label>{t('ownerLastName')}<input value={edit.ownerLastName} disabled={closed} onChange={(e) => setEdit({ ...edit, ownerLastName: e.target.value })} /></label>
                <label>{t('ownerFirstName')}<input value={edit.ownerFirstName} disabled={closed} onChange={(e) => setEdit({ ...edit, ownerFirstName: e.target.value })} /></label>
                <label>{t('model')}<input value={edit.model} disabled={closed} onChange={(e) => setEdit({ ...edit, model: e.target.value })} /></label>
                <label>{t('defaultDriver')}
                  <select value={edit.defaultDriverId} disabled={closed} onChange={(e) => setEdit({ ...edit, defaultDriverId: e.target.value })}>
                    <option value="">—</option>
                    {drivers.filter((d) => d.active).map((d) => <option key={d.id} value={d.id}>{d.fullName}</option>)}
                  </select>
                </label>
              </div>
              <div className="row">
                <button className="primary" disabled={act.busy || closed || !edit.fleetNumber.trim()} onClick={saveEdit}>{t('save')}</button>
                <span className="spacer" />
                {!closed && <button onClick={toggle} disabled={act.busy}>{t(tr.active ? 'disable' : 'enable')}</button>}
              </div>
            </Section>

            {tr.approvalStatus === 'APPROVED' && (
              <Section title={`${t('tripPrice')}: ${money(tr.price)}`}>
                <div className="row">
                  <label>{t('changePrice')}<input type="number" min={0} step="0.01" className="narrow-wide" value={price.value}
                    onChange={(e) => setPrice({ ...price, value: e.target.value })} /></label>
                  <label className="grow">{t('priceReason')} *<input value={price.reason} onChange={(e) => setPrice({ ...price, reason: e.target.value })} /></label>
                  <button className="primary" disabled={act.busy || !(Number(price.value) > 0) || !price.reason.trim()} onClick={savePrice}>{t('save')}</button>
                </div>
                <table>
                  <thead><tr><th>{t('price')}</th><th>{t('from')}</th><th>{t('priceReason')}</th><th /></tr></thead>
                  <tbody>{h.prices.map((p) => (
                    <tr key={p.id}><td className="num"><strong>{money(p.price)}</strong></td><td className="small">{fmtTime(p.validFrom, lang)}</td>
                      <td className="small">{p.reason ?? '—'}</td><td className="small muted">{p.setByName}</td></tr>
                  ))}</tbody>
                </table>
              </Section>
            )}

            <Section title={t('plates')}>
              {!closed && (
                <div className="row">
                  <label>{t('changePlate')}<input className="code" inputMode="numeric" value={plate} onChange={(e) => setPlate(e.target.value)} /></label>
                  <button disabled={act.busy || !plate.trim()} onClick={savePlate}>{t('save')}</button>
                </div>
              )}
              <table><tbody>{h.plates.map((p, i) => (
                <tr key={i}><td className="code"><strong>{p.plateText}</strong></td><td className="small">{fmtTime(p.validFrom, lang)} → {p.validTo ? fmtTime(p.validTo, lang) : '…'}</td></tr>
              ))}</tbody></table>
            </Section>

            <Section title={t('associatedDrivers')}>
              <table><tbody>{h.drivers.map((d) => (
                <tr key={d.driverId}><td>{d.driverName} {d.isDefault && <span className="badge info">{t('usualDriver')}</span>}</td><td className="num">{d.trips} {t('trips')}</td></tr>
              ))}</tbody></table>
            </Section>

            <Section title={t('truckHistory')}>
              <table>
                <tbody>{h.trips.map((x) => (
                  <tr key={x.tripId}>
                    <td className="code">{x.tripNumber}</td><td className="small">{fmtTime(x.enteredAt, lang)}</td>
                    <td className="small">{x.exitedAt ? fmtTime(x.exitedAt, lang) : '—'}</td><td>{x.driverName ?? '—'}</td>
                    <td>{x.loadStatus ? t(`d${x.loadStatus}` as Key) : ''}</td><td>{t(`state${x.state}` as Key)}</td>
                    {x.truckId !== tr.id && <td className="small muted">#{x.truckId}</td>}
                  </tr>
                ))}</tbody>
              </table>
            </Section>

            <Section title={t('auditTrail')}>
              <ol className="timeline">
                {h.audit.map((a) => (
                  <li key={a.id}><span className="muted small">{fmtTime(a.at, lang)}</span> {reason(a.action)}
                    <span className="muted small"> · {a.actor}</span></li>
                ))}
              </ol>
            </Section>
          </div>
        )}
      </aside>
    </>
  )
}

/** Supervisor: trucks waiting for approval, registered trucks with prices and history, drivers. */
export default function TrucksPage({ onChanged }: { onChanged?: () => void }) {
  const { t, lang } = useI18n()
  const money = useMoney()
  const [view, setView] = useState<View>('pending')
  const [pending, setPending] = useState<FleetTruck[] | null>(null)
  const [trucks, setTrucks] = useState<FleetTruck[] | null>(null)
  const [drivers, setDrivers] = useState<Driver[]>([])
  const [defaultPrice, setDefaultPrice] = useState('')
  const [status, setStatus] = useState<string>('APPROVED')
  const [search, setSearch] = useState('')
  const [openId, setOpenId] = useState<number | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [add, setAdd] = useState({ fleetNumber: '', ownerLastName: '', ownerFirstName: '', plate: '', price: '', model: '' })
  const [d, setD] = useState({ fullName: '', phone: '', employmentType: 'EMPLOYEE' })
  const act = useAction()

  const loadPending = useCallback(async () => setPending(await api.get<FleetTruck[]>('/api/fleet/trucks?status=PENDING_APPROVAL')), [])
  const loadTrucks = useCallback(async () => {
    const q = new URLSearchParams()
    if (status) q.set('status', status)
    if (search.trim()) q.set('search', search.trim())
    setTrucks(await api.get<FleetTruck[]>(`/api/fleet/trucks?${q}`))
  }, [status, search])
  const loadDrivers = useCallback(async () => setDrivers(await api.get<Driver[]>('/api/fleet/drivers')), [])

  useEffect(() => {
    act.run(async () => {
      await Promise.all([loadPending(), loadDrivers()])
      const settings = await api.get<SettingItem[]>('/api/settings')
      const p = settings.find((s) => s.key === 'trip.price_per_trip')
      if (p) setDefaultPrice(String(JSON.parse(p.value)))
    })
    const timer = setInterval(() => { loadPending().catch(() => undefined) }, 30_000)
    return () => clearInterval(timer)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (view === 'registered') act.run(loadTrucks) }, [view, status]) // eslint-disable-line react-hooks/exhaustive-deps

  const decided = async (r: Decision) => {
    setNotice(`#${r.truckId}: ${t(`ts${r.status}` as Key)}${r.reviewsResolved ? ` · ${r.reviewsResolved} ${t('tabReview')}` : ''}`)
    await loadPending()
    if (view === 'registered') await loadTrucks()
    onChanged?.()
  }
  const addTruck = () => act.run(async () => {
    await api.post('/api/fleet/trucks', {
      id: null, fleetNumber: add.fleetNumber.trim(), ownerLastName: add.ownerLastName.trim(), ownerFirstName: add.ownerFirstName.trim(),
      plate: add.plate.trim(), price: Number(add.price || defaultPrice), model: add.model.trim() || null, defaultDriverId: null,
    })
    setAdd({ fleetNumber: '', ownerLastName: '', ownerFirstName: '', plate: '', price: '', model: '' })
    setAdding(false)
    setNotice(t('saved'))
    await loadTrucks()
  })
  const addDriver = () => act.run(async () => {
    await api.post('/api/fleet/drivers', { ...d, phone: d.phone || null })
    setD({ fullName: '', phone: '', employmentType: 'EMPLOYEE' })
    await loadDrivers()
  })

  const approved = (trucks ?? []).filter((x) => x.approvalStatus === 'APPROVED')
  const [mergeTargets, setMergeTargets] = useState<FleetTruck[]>([])
  useEffect(() => {
    if (pending?.length) api.get<FleetTruck[]>('/api/fleet/trucks?status=APPROVED').then(setMergeTargets).catch(() => undefined)
  }, [pending])

  return (
    <div className="stack">
      <div className="tabs">
        <button className={view === 'pending' ? 'active' : ''} onClick={() => setView('pending')}>
          {t('pendingTrucks')}{pending?.length ? <span className="nav-count">{pending.length}</span> : null}
        </button>
        <button className={view === 'registered' ? 'active' : ''} onClick={() => setView('registered')}>{t('registeredTrucks')}</button>
        <button className={view === 'drivers' ? 'active' : ''} onClick={() => setView('drivers')}>{t('drivers')}</button>
      </div>
      <ErrorBox error={act.error} />
      {notice && <div className="notice" onClick={() => setNotice(null)}>{notice}</div>}

      {view === 'pending' && (
        <>
          {pending === null && <div className="loading">{t('loading')}</div>}
          {pending?.length === 0 && <div className="card empty"><strong>{t('noPending')}</strong></div>}
          <div className="pending-grid">
            {pending?.map((x) => (
              <PendingCard key={x.id} truck={x} approved={mergeTargets.length ? mergeTargets : approved} defaultPrice={defaultPrice} onDone={decided} />
            ))}
          </div>
        </>
      )}

      {view === 'registered' && (
        <section className="card flush">
          <form className="filters" onSubmit={(e) => { e.preventDefault(); act.run(loadTrucks) }}>
            <label>{t('statusLabel')}
              <select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="">{t('all')}</option>
                {(['APPROVED', 'PENDING_APPROVAL', 'REJECTED', 'MERGED'] as const).map((s) => <option key={s} value={s}>{t(`ts${s}` as Key)}</option>)}
              </select>
            </label>
            <label className="grow">{t('searchTrucks')}<input value={search} onChange={(e) => setSearch(e.target.value)} /></label>
            <button className="primary" disabled={act.busy}>{t('show')}</button>
            <button type="button" onClick={() => setAdding(!adding)}>{t('addTruckManual')}</button>
          </form>
          {adding && (
            <div className="filters">
              <label>{t('truckNumber')} *<input value={add.fleetNumber} onChange={(e) => setAdd({ ...add, fleetNumber: e.target.value })} /></label>
              <label>{t('ownerLastName')} *<input value={add.ownerLastName} onChange={(e) => setAdd({ ...add, ownerLastName: e.target.value })} /></label>
              <label>{t('ownerFirstName')} *<input value={add.ownerFirstName} onChange={(e) => setAdd({ ...add, ownerFirstName: e.target.value })} /></label>
              <label>{t('matricule')} *<input className="code" inputMode="numeric" value={add.plate} onChange={(e) => setAdd({ ...add, plate: e.target.value })} /></label>
              <label>{t('tripPrice')} *<input type="number" min={0} step="0.01" className="narrow-wide" value={add.price || defaultPrice}
                onChange={(e) => setAdd({ ...add, price: e.target.value })} /></label>
              <label>{t('model')}<input value={add.model} onChange={(e) => setAdd({ ...add, model: e.target.value })} /></label>
              <button className="primary" disabled={act.busy || !add.fleetNumber.trim() || !add.ownerLastName.trim() || !add.ownerFirstName.trim()
                || !add.plate.trim() || !(Number(add.price || defaultPrice) > 0)}
                onClick={addTruck}>{t('add')}</button>
            </div>
          )}
          {trucks === null && <div className="loading">{t('loading')}</div>}
          {trucks?.length === 0 && <div className="empty">{t('none')}</div>}
          {trucks && trucks.length > 0 && (
            <div className="table-scroll">
              <table>
                <thead><tr>
                  <th>{t('truckNumber')}</th><th>{t('ownerLastName')}</th><th>{t('ownerFirstName')}</th><th>{t('matricule')}</th>
                  <th className="num">{t('tripPrice')}</th><th>{t('statusLabel')}</th><th>{t('source')}</th><th className="num">{t('tripsCount')}</th>
                  <th>{t('lastTrip')}</th><th>{t('defaultDriver')}</th>
                </tr></thead>
                <tbody>
                  {trucks.map((x) => (
                    <tr key={x.id} className={`clickable ${x.active ? '' : 'inactive'}`} onClick={() => setOpenId(x.id)}>
                      <td><strong>{x.fleetNumber}</strong> <span className="muted small">#{x.id}</span></td>
                      <td>{x.ownerLastName ?? <span className="warn small">{t('incomplete')}</span>}</td>
                      <td>{x.ownerFirstName ?? ''}</td>
                      <td className="code">{x.plate ?? x.reportedPlate ?? '—'}</td>
                      <td className="num">{money(x.price)}</td>
                      <td><span className={`badge ${statusTone[x.approvalStatus]}`}>{t(`ts${x.approvalStatus}` as Key)}</span>
                        {x.mergedInto && <span className="muted small"> → #{x.mergedInto}</span>}</td>
                      <td className="small">{t(`via${x.createdVia}` as Key)}</td>
                      <td className="num">{x.tripCount}</td>
                      <td className="small">{fmtTime(x.lastTripAt, lang)}</td>
                      <td>{x.defaultDriverName ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {view === 'drivers' && (
        <Section title={t('drivers')}>
          <table>
            <thead><tr><th>#</th><th>{t('fullName')}</th><th>{t('phone')}</th><th /></tr></thead>
            <tbody>
              {drivers.map((x) => (
                <tr key={x.id} className={x.active ? '' : 'inactive'}><td>{x.id}</td><td>{x.fullName}</td><td className="ltr">{x.phone ?? ''}</td>
                  <td className="muted">{t(x.employmentType === 'CONTRACTOR' ? 'contractor' : 'employee')}</td></tr>
              ))}
            </tbody>
          </table>
          <div className="row">
            <label>{t('fullName')}<input value={d.fullName} onChange={(e) => setD({ ...d, fullName: e.target.value })} /></label>
            <label>{t('phone')}<input value={d.phone} onChange={(e) => setD({ ...d, phone: e.target.value })} /></label>
            <select value={d.employmentType} onChange={(e) => setD({ ...d, employmentType: e.target.value })}>
              <option value="EMPLOYEE">{t('employee')}</option>
              <option value="CONTRACTOR">{t('contractor')}</option>
            </select>
            <button className="primary" disabled={act.busy || !d.fullName.trim()} onClick={addDriver}>{t('add')}</button>
          </div>
        </Section>
      )}

      {openId !== null && (
        <TruckDrawer id={openId} drivers={drivers} onClose={() => setOpenId(null)}
          onChanged={() => { loadTrucks().catch(() => undefined); onChanged?.() }} />
      )}
    </div>
  )
}
