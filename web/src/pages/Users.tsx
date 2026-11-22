import { useEffect, useState } from 'react'
import { api, type Role, type UserItem } from '../api'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

const roles: Role[] = ['GATEKEEPER', 'QUARRY_OPERATOR', 'SUPERVISOR', 'ACCOUNTANT', 'MANAGER', 'AUDITOR', 'ADMIN']

/** Admin: accounts. Gatekeepers and quarry operators use a PIN, other roles a password of 10+ characters. */
export default function UsersPage() {
  const { t, lang } = useI18n()
  const [users, setUsers] = useState<UserItem[]>([])
  const [n, setN] = useState({ username: '', fullName: '', role: 'GATEKEEPER' as Role, language: 'ar', secret: '' })
  const [secrets, setSecrets] = useState<Record<number, string>>({})
  const act = useAction()

  const reload = async () => setUsers(await api.get<UserItem[]>('/api/admin/users'))
  useEffect(() => { act.run(reload) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const add = () => act.run(async () => {
    await api.post('/api/admin/users', { ...n, secret: n.secret || null })
    setN({ ...n, username: '', fullName: '', secret: '' })
    await reload()
  })
  const run = (fn: () => Promise<unknown>) => act.run(async () => { await fn(); await reload() })

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <Section title={t('users')}>
        <table>
          <thead><tr><th>{t('username')}</th><th>{t('fullName')}</th><th>{t('role')}</th><th>{t('language')}</th>
            <th>{t('lastLogin')}</th><th /></tr></thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.id} className={u.active ? '' : 'inactive'}>
                <td>{u.username}</td><td>{u.fullName}</td><td>{t(`r${u.role}` as Key)}</td><td>{u.language}</td>
                <td className="small">{fmtTime(u.lastLoginAt, lang)}{u.lockedUntil && <span className="warn"> · {t('locked')}</span>}
                  {!u.hasSecret && <span className="warn"> · {t('setSecret')}</span>}</td>
                <td>
                  <div className="row">
                    <input type="password" className="narrow-wide" placeholder={t('setSecret')} value={secrets[u.id] ?? ''}
                      onChange={(e) => setSecrets((s) => ({ ...s, [u.id]: e.target.value }))} />
                    <button disabled={!secrets[u.id]} onClick={() => run(async () => {
                      await api.put(`/api/admin/users/${u.id}/secret`, { secret: secrets[u.id] })
                      setSecrets((s) => ({ ...s, [u.id]: '' }))
                    })}>{t('save')}</button>
                    <button onClick={() => run(() => api.put(`/api/admin/users/${u.id}/active`, { active: !u.active }))}>
                      {t(u.active ? 'disable' : 'enable')}
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="row">
          <label>{t('username')}<input value={n.username} onChange={(e) => setN({ ...n, username: e.target.value })} /></label>
          <label>{t('fullName')}<input value={n.fullName} onChange={(e) => setN({ ...n, fullName: e.target.value })} /></label>
          <label>{t('role')}
            <select value={n.role} onChange={(e) => setN({ ...n, role: e.target.value as Role })}>
              {roles.map((r) => <option key={r} value={r}>{t(`r${r}` as Key)}</option>)}
            </select>
          </label>
          <label>{t('language')}
            <select value={n.language} onChange={(e) => setN({ ...n, language: e.target.value })}>
              <option value="ar">العربية</option><option value="fr">Français</option>
            </select>
          </label>
          <label>{t('secret')}<input type="password" value={n.secret} onChange={(e) => setN({ ...n, secret: e.target.value })} /></label>
          <button className="primary" disabled={!n.username || !n.fullName} onClick={add}>{t('add')}</button>
        </div>
      </Section>
    </div>
  )
}
