import { useEffect, useState } from 'react'
import { api, type Camera, type Discovered, type Gate, type OnvifInfo, type OnvifProfile } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

type CamRole = Camera['role']
const roles: CamRole[] = ['PLATE', 'CARGO', 'OVERVIEW']

/**
 * Admin: find cameras on the network (ONVIF discovery), connect, preview each channel and choose which
 * ones take pictures when a ticket is scanned, with their role (plate / cargo / overview).
 */
export default function CamerasPage() {
  const { t, lang } = useI18n()
  const [cameras, setCameras] = useState<Camera[]>([])
  const [gates, setGates] = useState<Gate[]>([])
  const [found, setFound] = useState<Discovered[] | null>(null)
  const [onvifUrl, setOnvifUrl] = useState('')
  const [user, setUser] = useState('admin')
  const [pass, setPass] = useState('')
  const [info, setInfo] = useState<OnvifInfo | null>(null)
  const [previews, setPreviews] = useState<Record<string, string>>({})
  const [manualUrl, setManualUrl] = useState('')
  const [gateCode, setGateCode] = useState('')
  const [gateName, setGateName] = useState('')
  const act = useAction()

  const reload = async () => {
    setCameras(await api.get<Camera[]>('/api/admin/cameras'))
    setGates(await api.get<Gate[]>('/api/admin/gates'))
  }
  useEffect(() => { act.run(reload) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const discover = () => act.run(async () => setFound(await api.post<Discovered[]>('/api/admin/cameras/discover')))
  const connect = () => act.run(async () => {
    setInfo(null)
    setInfo(await api.post<OnvifInfo>('/api/admin/cameras/probe', { onvifUrl, username: user, password: pass }))
  })
  const preview = (key: string, snapshotUrl: string) => act.run(async () => {
    const url = await api.image('POST', '/api/admin/cameras/test', { snapshotUrl, username: user, password: pass })
    setPreviews((p) => ({ ...p, [key]: url }))
  })
  const testExisting = (c: Camera) => act.run(async () => {
    const url = await api.image('GET', `/api/admin/cameras/${c.id}/test`)
    setPreviews((p) => ({ ...p, [`cam${c.id}`]: url }))
  })

  const addOnvif = (p: OnvifProfile, role: CamRole, gateId: number | null) => act.run(async () => {
    await api.post('/api/admin/cameras', {
      label: p.name, gateId, role, captureOnScan: true, frames: 3, frameIntervalMs: 300, source: 'ONVIF', onvifUrl,
      profileToken: p.token, snapshotUrl: p.snapshotUri, username: user, password: pass,
      manufacturer: info?.manufacturer, model: info?.model, serial: info?.serial,
    })
    await reload()
  })
  const addManual = (role: CamRole, gateId: number | null) => act.run(async () => {
    await api.post('/api/admin/cameras', {
      label: manualUrl, gateId, role, captureOnScan: true, frames: 3, frameIntervalMs: 300, source: 'URL',
      snapshotUrl: manualUrl, username: user, password: pass,
    })
    setManualUrl('')
    await reload()
  })
  const update = (c: Camera, change: Record<string, unknown>) => act.run(async () => {
    await api.put(`/api/admin/cameras/${c.id}`, { clearGate: false, ...change })
    await reload()
  })
  const createGate = () => act.run(async () => {
    await api.post('/api/admin/gates', { code: gateCode, name: gateName })
    setGateCode(''); setGateName('')
    await reload()
  })

  const defaultGate = gates[0]?.id ?? null

  return (
    <div className="stack">
      <ErrorBox error={act.error} />

      <Section title={t('cameras')}>
        <table>
          <thead><tr>
            <th>{t('label')}</th><th>{t('role')}</th><th>{t('gate')}</th><th>{t('captureOnScan')}</th><th>{t('frames')}</th>
            <th>{t('lastOk')}</th><th>{t('lastError')}</th><th>{t('version')}</th><th />
          </tr></thead>
          <tbody>
            {cameras.map((c) => (
              <tr key={c.id} className={c.active ? '' : 'inactive'}>
                <td>{c.label}<div className="muted small">{c.manufacturer} {c.model}</div></td>
                <td>
                  <select value={c.role} onChange={(e) => update(c, { role: e.target.value })}>
                    {roles.map((r) => <option key={r} value={r}>{t(`role${r}` as Key)}</option>)}
                  </select>
                </td>
                <td>
                  <select value={c.gateId ?? ''} onChange={(e) => update(c, e.target.value ? { gateId: Number(e.target.value) } : { clearGate: true })}>
                    <option value="">—</option>
                    {gates.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
                  </select>
                </td>
                <td><input type="checkbox" checked={c.captureOnScan} onChange={(e) => update(c, { captureOnScan: e.target.checked })} /></td>
                <td>
                  <input type="number" min={1} max={10} value={c.frames} className="narrow"
                    onChange={(e) => update(c, { frames: Number(e.target.value) })} />
                </td>
                <td className="small">{fmtTime(c.lastOkAt, lang)}</td>
                <td className="small warn">{c.lastError ? `${fmtTime(c.lastErrorAt, lang)} ${c.lastError}` : ''}</td>
                <td>{c.configVersion}</td>
                <td>
                  <button onClick={() => testExisting(c)}>{t('test')}</button>
                  <button onClick={() => update(c, { active: !c.active })}>{t(c.active ? 'disable' : 'enable')}</button>
                  {previews[`cam${c.id}`] && <img className="thumb" src={previews[`cam${c.id}`]} alt={c.label} />}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title={t('discover')} actions={<button className="primary" disabled={act.busy} onClick={discover}>
        {act.busy ? t('discovering') : t('discover')}</button>}>
        {found && found.length === 0 && <p className="muted">{t('noneFound')}</p>}
        {found && found.length > 0 && (
          <table>
            <thead><tr><th>{t('name')}</th><th>IP</th><th>{t('model')}</th><th>{t('onvifAddress')}</th><th /></tr></thead>
            <tbody>
              {found.map((d) => (
                <tr key={d.endpointId}>
                  <td>{d.name ?? '—'}</td><td>{d.fromIp}</td><td>{d.hardware ?? '—'}</td><td className="small">{d.xAddrs[0]}</td>
                  <td><button onClick={() => { setOnvifUrl(d.xAddrs[0]); setInfo(null) }}>{t('use')}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <div className="row">
          <label>{t('onvifAddress')}<input value={onvifUrl} onChange={(e) => setOnvifUrl(e.target.value)}
            placeholder="http://192.168.1.60/onvif/device_service" className="wide" /></label>
          <label>{t('camUser')}<input value={user} onChange={(e) => setUser(e.target.value)} /></label>
          <label>{t('camPassword')}<input type="password" value={pass} onChange={(e) => setPass(e.target.value)} /></label>
          <button className="primary" disabled={act.busy || !onvifUrl} onClick={connect}>{t('connect')}</button>
        </div>

        {info && (
          <>
            <p><strong>{info.manufacturer} {info.model}</strong> <span className="muted">{info.serial}</span></p>
            <h3>{t('channels')}</h3>
            <div className="profiles">
              {info.profiles.map((p) => (
                <ProfileCard key={p.token} profile={p} preview={previews[p.token]} gates={gates} defaultGate={defaultGate}
                  onPreview={() => p.snapshotUri && preview(p.token, p.snapshotUri)} onAdd={(role, gateId) => addOnvif(p, role, gateId)} />
              ))}
            </div>
          </>
        )}
      </Section>

      <Section title={t('manualCamera')}>
        <div className="row">
          <label>{t('snapshotUrl')}<input value={manualUrl} onChange={(e) => setManualUrl(e.target.value)} className="wide"
            placeholder="http://192.168.1.64/ISAPI/Streaming/channels/101/picture" /></label>
          <label>{t('camUser')}<input value={user} onChange={(e) => setUser(e.target.value)} /></label>
          <label>{t('camPassword')}<input type="password" value={pass} onChange={(e) => setPass(e.target.value)} /></label>
          <button disabled={!manualUrl} onClick={() => preview('manual', manualUrl)}>{t('preview')}</button>
        </div>
        {previews.manual && (
          <ProfileCard profile={{ token: 'manual', name: manualUrl, width: null, height: null, snapshotUri: manualUrl }}
            preview={previews.manual} gates={gates} defaultGate={defaultGate} onPreview={() => preview('manual', manualUrl)}
            onAdd={(role, gateId) => addManual(role, gateId)} />
        )}
      </Section>

      <Section title={t('gates')}>
        <ul>{gates.map((g) => <li key={g.id}>{g.code} — {g.name}</li>)}</ul>
        <div className="row">
          <label>{t('code')}<input value={gateCode} onChange={(e) => setGateCode(e.target.value)} /></label>
          <label>{t('name')}<input value={gateName} onChange={(e) => setGateName(e.target.value)} /></label>
          <button disabled={!gateCode || !gateName} onClick={createGate}>{t('newGate')}</button>
        </div>
      </Section>
    </div>
  )
}

function ProfileCard({ profile, preview, gates, defaultGate, onPreview, onAdd }: {
  profile: OnvifProfile; preview?: string; gates: Gate[]; defaultGate: number | null
  onPreview: () => void; onAdd: (role: CamRole, gateId: number | null) => void
}) {
  const { t } = useI18n()
  const [role, setRole] = useState<CamRole>('PLATE')
  const [gateId, setGateId] = useState<number | null>(defaultGate)
  return (
    <div className="profile">
      <strong>{profile.name}</strong>
      <div className="muted small">{profile.width && `${profile.width}×${profile.height}`}</div>
      {preview ? <img className="preview" src={preview} alt={profile.name} /> : <div className="preview empty" />}
      {!profile.snapshotUri && <div className="warn small">{t('noSnapshot')}</div>}
      <div className="row">
        <button disabled={!profile.snapshotUri} onClick={onPreview}>{t('preview')}</button>
        <select value={role} onChange={(e) => setRole(e.target.value as CamRole)}>
          {roles.map((r) => <option key={r} value={r}>{t(`role${r}` as Key)}</option>)}
        </select>
        <select value={gateId ?? ''} onChange={(e) => setGateId(e.target.value ? Number(e.target.value) : null)}>
          <option value="">—</option>
          {gates.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
        </select>
        <button className="primary" disabled={!profile.snapshotUri} onClick={() => onAdd(role, gateId)}>{t('add')}</button>
      </div>
    </div>
  )
}
