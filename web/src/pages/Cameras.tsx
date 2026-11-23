import { useEffect, useState } from 'react'
import { api, type Camera, type Discovered, type Gate, type OnvifInfo, type OnvifProfile, type Role, type StorageStatus } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

type CamRole = Camera['role']
const roles: CamRole[] = ['PLATE', 'CARGO', 'OVERVIEW']
const gb = (b: number) => `${(b / 1_073_741_824).toFixed(1)} GB`
/** Credentials never go in the address: they are stored (encrypted) in the camera's own fields. */
const withoutCredentials = (u: string | null) => (u ?? '').replace(/^(rtsp:\/\/)[^@/]*@/i, '$1')

function RecorderBadge({ c }: { c: Camera }) {
  const { t, lang } = useI18n()
  if (!c.recordVideo) return <span className="muted small">{t('recOFF')}</span>
  const s = c.recorderStatus ?? 'OFF'
  const tone = s === 'RECORDING' ? 'ok' : s === 'ERROR' ? 'bad' : s === 'STARTING' ? 'warn' : ''
  return (
    <div>
      <span className={`badge ${tone}`}>{t(`rec${s}` as Key)}</span>
      {c.lastFrameAt && <div className="muted small">{t('lastFrame')}: {fmtTime(c.lastFrameAt, lang)}</div>}
      {c.recorderError && <div className="bad small ltr">{c.recorderError}</div>}
    </div>
  )
}

/**
 * Supervisor / administrator: cameras at the gates — pictures taken at each scan, continuous recording for
 * the passage video (RTSP), recorder health, storage, and ONVIF discovery to add new cameras.
 */
export default function CamerasPage({ role }: { role: Role }) {
  const { t, lang } = useI18n()
  const [cameras, setCameras] = useState<Camera[] | null>(null)
  const [gates, setGates] = useState<Gate[]>([])
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [found, setFound] = useState<Discovered[] | null>(null)
  const [onvifUrl, setOnvifUrl] = useState('')
  const [user, setUser] = useState('admin')
  const [pass, setPass] = useState('')
  const [info, setInfo] = useState<OnvifInfo | null>(null)
  const [previews, setPreviews] = useState<Record<string, string>>({})
  const [manualUrl, setManualUrl] = useState('')
  const [manualStream, setManualStream] = useState('')
  const [streams, setStreams] = useState<Record<number, string>>({})
  const [gateCode, setGateCode] = useState('')
  const [gateName, setGateName] = useState('')
  const act = useAction()
  const isAdmin = role === 'ADMIN'

  const reload = async () => {
    setCameras(await api.get<Camera[]>('/api/admin/cameras'))
    setGates(await api.get<Gate[]>('/api/gate/gates'))
    setStorage(await api.get<StorageStatus>('/api/storage').catch(() => null))
  }
  useEffect(() => {
    act.run(reload)
    const timer = setInterval(() => {
      api.get<Camera[]>('/api/admin/cameras').then(setCameras).catch(() => undefined)
    }, 15_000)
    return () => clearInterval(timer)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

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

  const addOnvif = (p: OnvifProfile, r: CamRole, gateId: number | null, record: boolean) => act.run(async () => {
    await api.post('/api/admin/cameras', {
      label: p.name, gateId, role: r, captureOnScan: true, frames: 3, frameIntervalMs: 300, source: 'ONVIF', onvifUrl,
      profileToken: p.token, snapshotUrl: p.snapshotUri, username: user, password: pass,
      manufacturer: info?.manufacturer, model: info?.model, serial: info?.serial,
      streamUrl: p.streamUri ? withoutCredentials(p.streamUri) : null, recordVideo: record && !!p.streamUri,
    })
    await reload()
  })
  const addManual = (r: CamRole, gateId: number | null, record: boolean) => act.run(async () => {
    await api.post('/api/admin/cameras', {
      label: manualUrl, gateId, role: r, captureOnScan: true, frames: 3, frameIntervalMs: 300, source: 'URL',
      snapshotUrl: manualUrl, username: user, password: pass,
      streamUrl: manualStream.trim() ? withoutCredentials(manualStream.trim()) : null, recordVideo: record && !!manualStream.trim(),
    })
    setManualUrl(''); setManualStream('')
    await reload()
  })
  const update = (c: Camera, change: Record<string, unknown>) => act.run(async () => {
    await api.put(`/api/admin/cameras/${c.id}`, { clearGate: false, ...change })
    await reload()
  })
  const saveStream = (c: Camera) => {
    const v = (streams[c.id] ?? '').trim()
    update(c, v ? { streamUrl: v } : { clearStream: true, recordVideo: false })
    setStreams((s) => { const n = { ...s }; delete n[c.id]; return n })
  }
  const createGate = () => act.run(async () => {
    await api.post('/api/admin/gates', { code: gateCode, name: gateName })
    setGateCode(''); setGateName('')
    await reload()
  })

  const defaultGate = gates[0]?.id ?? null
  const usedPct = storage && storage.totalBytes ? 100 - (storage.freeBytes / storage.totalBytes) * 100 : 0

  return (
    <div className="stack">
      <ErrorBox error={act.error} />

      <section className="card flush">
        <div className="card-head"><h2>{t('cameras')}</h2></div>
        {cameras === null && <div className="loading">{t('loading')}</div>}
        {cameras && (
          <div className="table-scroll">
            <table>
              <thead><tr>
                <th>{t('label')} / {t('location')}</th><th>{t('role')}</th><th>{t('gate')}</th><th>{t('captureOnScan')}</th><th>{t('frames')}</th>
                <th>{t('streamUrl')}</th><th>{t('recordVideo')}</th><th>{t('recorder')}</th><th>{t('recordings24h')}</th>
                <th>{t('lastOk')} / {t('lastError')}</th><th />
              </tr></thead>
              <tbody>
                {cameras.map((c) => (
                  <tr key={c.id} className={c.active ? '' : 'inactive'}>
                    <td>
                      <strong>{c.label}</strong>
                      <div className="muted small">{c.manufacturer} {c.model} · v{c.configVersion}</div>
                      <input className="narrow-wide small" placeholder={t('location')} defaultValue={c.location ?? ''}
                        onBlur={(e) => { if (e.target.value.trim() !== (c.location ?? '')) update(c, { location: e.target.value.trim() }) }} />
                    </td>
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
                    <td>
                      <div className="row nowrap">
                        <input className="ltr stream-input" placeholder="rtsp://192.168.1.64:554/Streaming/Channels/102"
                          value={streams[c.id] ?? c.streamUrl ?? ''} onChange={(e) => setStreams({ ...streams, [c.id]: e.target.value })} />
                        {streams[c.id] !== undefined && streams[c.id] !== (c.streamUrl ?? '') && (
                          <button className="primary small-btn" onClick={() => saveStream(c)}>{t('save')}</button>
                        )}
                      </div>
                    </td>
                    <td><input type="checkbox" checked={c.recordVideo} disabled={!c.streamUrl}
                      onChange={(e) => update(c, { recordVideo: e.target.checked })} /></td>
                    <td><RecorderBadge c={c} /></td>
                    <td className="small">
                      <span className="ok">{c.recordingsOk24h} ✓</span>
                      {c.recordingsFailed24h > 0 && <span className="bad"> · {c.recordingsFailed24h} ✗</span>}
                    </td>
                    <td className="small">
                      {fmtTime(c.lastOkAt, lang)}
                      {c.lastError && <div className="warn">{fmtTime(c.lastErrorAt, lang)} {c.lastError}</div>}
                    </td>
                    <td>
                      <div className="row nowrap">
                        <button className="small-btn" onClick={() => testExisting(c)}>{t('test')}</button>
                        <button className="small-btn" onClick={() => update(c, { active: !c.active })}>{t(c.active ? 'disable' : 'enable')}</button>
                      </div>
                      {previews[`cam${c.id}`] && <img className="thumb" src={previews[`cam${c.id}`]} alt={c.label} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small" style={{ padding: '0 16px 12px' }}>{t('streamHint')}</p>
      </section>

      <Section title={t('storage')} actions={storage?.low ? <span className="badge bad">{t('storageLow')}</span> : undefined}>
        {storage ? (
          <>
            <div className={`meter ${storage.low ? 'low' : ''}`}><div style={{ width: `${usedPct.toFixed(1)}%` }} /></div>
            <dl className="facts small">
              <dt>{t('freeSpace')}</dt><dd className="ltr">{gb(storage.freeBytes)} / {gb(storage.totalBytes)} (min. {storage.minFreeGb} GB)</dd>
              <dt>{t('videoSize')}</dt><dd><bdi>{gb(storage.videoBytes)}</bdi> · {storage.recordings} {t('videos')}</dd>
              <dt>{t('location')}</dt><dd className="ltr">{storage.root}</dd>
            </dl>
          </>
        ) : <div className="muted small">—</div>}
        <p className="muted small">{t('privacyNote')}</p>
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
                  <td>{d.name ?? '—'}</td><td className="ltr">{d.fromIp}</td><td>{d.hardware ?? '—'}</td><td className="small ltr">{d.xAddrs[0]}</td>
                  <td><button onClick={() => { setOnvifUrl(d.xAddrs[0]); setInfo(null) }}>{t('use')}</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <div className="row">
          <label>{t('onvifAddress')}<input value={onvifUrl} onChange={(e) => setOnvifUrl(e.target.value)}
            placeholder="http://192.168.1.60/onvif/device_service" className="wide ltr" /></label>
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
                  onPreview={() => p.snapshotUri && preview(p.token, p.snapshotUri)} onAdd={(r, g, rec) => addOnvif(p, r, g, rec)} />
              ))}
            </div>
          </>
        )}
      </Section>

      <Section title={t('manualCamera')}>
        <div className="row">
          <label>{t('snapshotUrl')}<input value={manualUrl} onChange={(e) => setManualUrl(e.target.value)} className="wide ltr"
            placeholder="http://192.168.1.64/ISAPI/Streaming/channels/101/picture" /></label>
          <label>{t('streamUrl')}<input value={manualStream} onChange={(e) => setManualStream(e.target.value)} className="wide ltr"
            placeholder="rtsp://192.168.1.64:554/Streaming/Channels/102" /></label>
          <label>{t('camUser')}<input value={user} onChange={(e) => setUser(e.target.value)} /></label>
          <label>{t('camPassword')}<input type="password" value={pass} onChange={(e) => setPass(e.target.value)} /></label>
          <button disabled={!manualUrl} onClick={() => preview('manual', manualUrl)}>{t('preview')}</button>
        </div>
        {previews.manual && (
          <ProfileCard profile={{ token: 'manual', name: manualUrl, width: null, height: null, snapshotUri: manualUrl, streamUri: manualStream || null }}
            preview={previews.manual} gates={gates} defaultGate={defaultGate} onPreview={() => preview('manual', manualUrl)}
            onAdd={(r, g, rec) => addManual(r, g, rec)} />
        )}
      </Section>

      <Section title={t('gates')}>
        <ul>{gates.map((g) => <li key={g.id}>{g.code} — {g.name}</li>)}</ul>
        {isAdmin && (
          <div className="row">
            <label>{t('code')}<input value={gateCode} onChange={(e) => setGateCode(e.target.value)} /></label>
            <label>{t('name')}<input value={gateName} onChange={(e) => setGateName(e.target.value)} /></label>
            <button disabled={!gateCode || !gateName} onClick={createGate}>{t('newGate')}</button>
          </div>
        )}
      </Section>
    </div>
  )
}

function ProfileCard({ profile, preview, gates, defaultGate, onPreview, onAdd }: {
  profile: OnvifProfile; preview?: string; gates: Gate[]; defaultGate: number | null
  onPreview: () => void; onAdd: (role: CamRole, gateId: number | null, record: boolean) => void
}) {
  const { t } = useI18n()
  const [r, setR] = useState<CamRole>('PLATE')
  const [gateId, setGateId] = useState<number | null>(defaultGate)
  const [record, setRecord] = useState(false)
  return (
    <div className="profile">
      <strong>{profile.name}</strong>
      <div className="muted small">{profile.width && `${profile.width}×${profile.height}`}</div>
      {preview ? <img className="preview" src={preview} alt={profile.name} /> : <div className="preview empty" />}
      {!profile.snapshotUri && <div className="warn small">{t('noSnapshot')}</div>}
      {profile.streamUri && <div className="muted small ltr">{withoutCredentials(profile.streamUri)}</div>}
      <div className="row">
        <button disabled={!profile.snapshotUri} onClick={onPreview}>{t('preview')}</button>
        <select value={r} onChange={(e) => setR(e.target.value as CamRole)}>
          {roles.map((x) => <option key={x} value={x}>{t(`role${x}` as Key)}</option>)}
        </select>
        <select value={gateId ?? ''} onChange={(e) => setGateId(e.target.value ? Number(e.target.value) : null)}>
          <option value="">—</option>
          {gates.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
        </select>
        <label className="inline small"><input type="checkbox" checked={record} disabled={!profile.streamUri}
          onChange={(e) => setRecord(e.target.checked)} /> {t('recordVideo')}</label>
        <button className="primary" disabled={!profile.snapshotUri} onClick={() => onAdd(r, gateId, record)}>{t('add')}</button>
      </div>
    </div>
  )
}
