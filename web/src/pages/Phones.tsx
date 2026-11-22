import { useEffect, useState } from 'react'
import QRCode from 'qrcode'
import { api, type Device, type Invite, type Site } from '../api'
import { useI18n } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

/** Admin: enroll quarry phones (one-time QR), revoke lost phones, quarry site and its boundary. */
export default function PhonesPage() {
  const { t, lang } = useI18n()
  const [devices, setDevices] = useState<Device[]>([])
  const [sites, setSites] = useState<Site[]>([])
  const [label, setLabel] = useState('')
  const [invite, setInvite] = useState<{ img: string; expiresAt: string } | null>(null)
  const [reasons, setReasons] = useState<Record<number, string>>({})
  const [siteCode, setSiteCode] = useState('')
  const [siteName, setSiteName] = useState('')
  const [siteKind, setSiteKind] = useState<'QUARRY' | 'FACTORY'>('QUARRY')
  const [fence, setFence] = useState<Record<number, string>>({})
  const act = useAction()

  const reload = async () => {
    setDevices(await api.get<Device[]>('/api/admin/devices'))
    setSites(await api.get<Site[]>('/api/admin/sites'))
  }
  useEffect(() => { act.run(reload) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const newCode = () => act.run(async () => {
    const inv = await api.post<Invite>('/api/admin/enrollments', { label })
    const img = await QRCode.toDataURL(inv.qrText, { errorCorrectionLevel: 'M', margin: 2, scale: 6 })
    setInvite({ img, expiresAt: inv.expiresAt })
    setLabel('')
  })
  const revoke = (d: Device) => act.run(async () => {
    await api.post(`/api/admin/devices/${d.id}/revoke`, { reason: reasons[d.id] ?? '' })
    await reload()
  })
  const createSite = () => act.run(async () => {
    await api.post('/api/admin/sites', { code: siteCode, name: siteName, kind: siteKind })
    setSiteCode(''); setSiteName('')
    await reload()
  })
  const saveFence = (s: Site) => act.run(async () => {
    const points = (fence[s.id] ?? '').split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
      const [lat, lon] = l.split(/[,;\s]+/).map(Number)
      return [Math.round(lat * 1e6), Math.round(lon * 1e6)]
    })
    await api.put(`/api/admin/sites/${s.id}/geofence`, { points })
    await reload()
  })

  return (
    <div className="stack">
      <ErrorBox error={act.error} />

      <Section title={t('newCode')}>
        <div className="row">
          <label>{t('codeFor')}<input value={label} onChange={(e) => setLabel(e.target.value)} /></label>
          <button className="primary" disabled={!label || act.busy} onClick={newCode}>{t('newCode')}</button>
        </div>
        {invite && (
          <div className="invite">
            <img src={invite.img} alt="enrollment QR" />
            <p>{t('scanWithPhone')}</p>
            <p className="muted">{t('expires')}: {fmtTime(invite.expiresAt, lang)}</p>
            <button onClick={() => { setInvite(null); act.run(reload) }}>{t('close')}</button>
          </div>
        )}
      </Section>

      <Section title={t('phones')}>
        <table>
          <thead><tr><th>#</th><th>{t('label')}</th><th>{t('enrolled')}</th><th>{t('lastSync')}</th><th>{t('revoked')}</th><th /></tr></thead>
          <tbody>
            {devices.map((d) => (
              <tr key={d.id} className={d.revokedAt ? 'inactive' : ''}>
                <td>{d.id}</td><td>{d.label}<div className="muted small">{d.kind}</div></td>
                <td>{fmtTime(d.enrolledAt, lang)}</td>
                <td>{fmtTime(d.lastSyncAt, lang)}</td>
                <td>{d.revokedAt ? `${fmtTime(d.revokedAt, lang)} — ${d.revokeReason}` : ''}</td>
                <td>{!d.revokedAt && (
                  <div className="row">
                    <input placeholder={t('revokeReason')} value={reasons[d.id] ?? ''}
                      onChange={(e) => setReasons((r) => ({ ...r, [d.id]: e.target.value }))} />
                    <button className="danger" disabled={!reasons[d.id]} onClick={() => revoke(d)}>{t('revoke')}</button>
                  </div>
                )}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title={t('sites')}>
        {sites.map((s) => (
          <div key={s.id} className="site">
            <strong>{s.code} — {s.name}</strong> <span className="muted">({t(s.kind === 'QUARRY' ? 'quarry' : 'factory')})</span>
            {s.kind === 'QUARRY' && (
              <>
                {s.hasGeofence && <span className="ok"> ✓ {t('hasGeofence')}</span>}
                <label>{t('geofence')}
                  <textarea rows={4} value={fence[s.id] ?? ''} placeholder={'35.0012, -0.6431\n35.0050, -0.6402\n35.0031, -0.6370'}
                    onChange={(e) => setFence((f) => ({ ...f, [s.id]: e.target.value }))} />
                </label>
                <button disabled={!fence[s.id]} onClick={() => saveFence(s)}>{t('saveGeofence')}</button>
              </>
            )}
          </div>
        ))}
        <div className="row">
          <label>{t('code')}<input value={siteCode} onChange={(e) => setSiteCode(e.target.value)} /></label>
          <label>{t('name')}<input value={siteName} onChange={(e) => setSiteName(e.target.value)} /></label>
          <select value={siteKind} onChange={(e) => setSiteKind(e.target.value as 'QUARRY' | 'FACTORY')}>
            <option value="QUARRY">{t('quarry')}</option>
            <option value="FACTORY">{t('factory')}</option>
          </select>
          <button disabled={!siteCode || !siteName} onClick={createSite}>{t('create')}</button>
        </div>
      </Section>
    </div>
  )
}
