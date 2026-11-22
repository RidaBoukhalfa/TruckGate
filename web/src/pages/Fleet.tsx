import { useEffect, useState } from 'react'
import { api, type Driver, type Truck } from '../api'
import { useI18n } from '../i18n'
import { ErrorBox, Section, useAction } from '../ui'

/** Admin: drivers and trucks (fleet number, current plate with history kept, usual driver). */
export default function FleetPage() {
  const { t } = useI18n()
  const [drivers, setDrivers] = useState<Driver[]>([])
  const [trucks, setTrucks] = useState<Truck[]>([])
  const [d, setD] = useState({ fullName: '', phone: '', employmentType: 'EMPLOYEE' })
  const [tr, setTr] = useState({ id: '', fleetNumber: '', model: '', plate: '', defaultDriverId: '' })
  const [plates, setPlates] = useState<Record<number, string>>({})
  const act = useAction()

  const reload = async () => {
    setDrivers(await api.get<Driver[]>('/api/admin/drivers'))
    setTrucks(await api.get<Truck[]>('/api/admin/trucks'))
  }
  useEffect(() => { act.run(reload) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const addDriver = () => act.run(async () => {
    await api.post('/api/admin/drivers', { ...d, phone: d.phone || null })
    setD({ fullName: '', phone: '', employmentType: 'EMPLOYEE' })
    await reload()
  })
  const addTruck = () => act.run(async () => {
    await api.post('/api/admin/trucks', {
      id: Number(tr.id), fleetNumber: tr.fleetNumber, model: tr.model || null, plate: tr.plate || null,
      defaultDriverId: tr.defaultDriverId ? Number(tr.defaultDriverId) : null,
    })
    setTr({ id: '', fleetNumber: '', model: '', plate: '', defaultDriverId: '' })
    await reload()
  })
  const run = (fn: () => Promise<unknown>) => act.run(async () => { await fn(); await reload() })

  return (
    <div className="stack">
      <ErrorBox error={act.error} />

      <Section title={t('trucks')}>
        <table>
          <thead><tr><th>#</th><th>{t('fleetNumber')}</th><th>{t('plate')}</th><th>{t('model')}</th><th>{t('defaultDriver')}</th><th /></tr></thead>
          <tbody>
            {trucks.map((x) => (
              <tr key={x.id} className={x.active ? '' : 'inactive'}>
                <td>{x.id}</td><td><strong>{x.fleetNumber}</strong></td><td>{x.plate ?? '—'}</td><td>{x.model ?? ''}</td>
                <td>
                  <select value={x.defaultDriverId ?? ''} onChange={(e) =>
                    run(() => api.put(`/api/admin/trucks/${x.id}/default-driver`, { driverId: e.target.value ? Number(e.target.value) : null }))}>
                    <option value="">—</option>
                    {drivers.filter((v) => v.active).map((v) => <option key={v.id} value={v.id}>{v.fullName}</option>)}
                  </select>
                </td>
                <td>
                  <div className="row">
                    <input placeholder={t('changePlate')} value={plates[x.id] ?? ''} className="narrow-wide"
                      onChange={(e) => setPlates((p) => ({ ...p, [x.id]: e.target.value }))} />
                    <button disabled={!plates[x.id]} onClick={() => run(async () => {
                      await api.put(`/api/admin/trucks/${x.id}/plate`, { plate: plates[x.id] })
                      setPlates((p) => ({ ...p, [x.id]: '' }))
                    })}>{t('save')}</button>
                    <button onClick={() => run(() => api.put(`/api/admin/trucks/${x.id}/active`, { active: !x.active }))}>
                      {t(x.active ? 'disable' : 'enable')}
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="row">
          <label>{t('truckId')}<input type="number" min={1} max={65535} value={tr.id} className="narrow" onChange={(e) => setTr({ ...tr, id: e.target.value })} /></label>
          <label>{t('fleetNumber')}<input value={tr.fleetNumber} className="narrow" onChange={(e) => setTr({ ...tr, fleetNumber: e.target.value })} /></label>
          <label>{t('plate')}<input value={tr.plate} onChange={(e) => setTr({ ...tr, plate: e.target.value })} placeholder="01234 216 16" /></label>
          <label>{t('model')}<input value={tr.model} onChange={(e) => setTr({ ...tr, model: e.target.value })} /></label>
          <label>{t('defaultDriver')}
            <select value={tr.defaultDriverId} onChange={(e) => setTr({ ...tr, defaultDriverId: e.target.value })}>
              <option value="">—</option>
              {drivers.filter((v) => v.active).map((v) => <option key={v.id} value={v.id}>{v.fullName}</option>)}
            </select>
          </label>
          <button className="primary" disabled={!tr.id || !tr.fleetNumber} onClick={addTruck}>{t('add')}</button>
        </div>
      </Section>

      <Section title={t('drivers')}>
        <table>
          <thead><tr><th>#</th><th>{t('fullName')}</th><th>{t('phone')}</th><th /></tr></thead>
          <tbody>
            {drivers.map((x) => (
              <tr key={x.id}><td>{x.id}</td><td>{x.fullName}</td><td>{x.phone ?? ''}</td>
                <td className="muted">{t(x.employmentType === 'CONTRACTOR' ? 'contractor' : 'employee')}
                  {trucks.filter((k) => k.defaultDriverId === x.id).map((k) => ` · ${k.fleetNumber}`)}</td></tr>
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
          <button className="primary" disabled={!d.fullName} onClick={addDriver}>{t('add')}</button>
        </div>
      </Section>
    </div>
  )
}
