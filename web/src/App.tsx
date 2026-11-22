import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, type Role, type User } from './api'
import { I18nContext, makeI18n, type Key, type Lang } from './i18n'
import Login from './pages/Login'
import GatePage from './pages/Gate'
import CamerasPage from './pages/Cameras'
import PhonesPage from './pages/Phones'
import FleetPage from './pages/Fleet'
import UsersPage from './pages/Users'
import IntegrityPage from './pages/Integrity'

type Tab = 'gate' | 'cameras' | 'phones' | 'fleet' | 'users' | 'integrity'

const tabs: { id: Tab; label: Key; roles: Role[] }[] = [
  { id: 'gate', label: 'tabGate', roles: ['GATEKEEPER', 'SUPERVISOR', 'ADMIN'] },
  { id: 'cameras', label: 'tabCameras', roles: ['ADMIN'] },
  { id: 'phones', label: 'tabPhones', roles: ['ADMIN'] },
  { id: 'fleet', label: 'tabFleet', roles: ['ADMIN'] },
  { id: 'users', label: 'tabUsers', roles: ['ADMIN'] },
  { id: 'integrity', label: 'tabIntegrity', roles: ['ADMIN', 'MANAGER', 'AUDITOR'] },
]

export default function App() {
  const [user, setUser] = useState<User | null | undefined>(undefined)
  const [lang, setLang] = useState<Lang>(() => (localStorage.getItem('tg.lang') as Lang) || 'ar')
  const [tab, setTab] = useState<Tab>('gate')
  const i18n = useMemo(() => makeI18n(lang), [lang])

  useEffect(() => {
    api.get<User>('/api/auth/me')
      .then((u) => { setUser(u); setLang(u.language) })
      .catch((e) => { if (e instanceof ApiError && e.status === 401) setUser(null); else setUser(null) })
  }, [])

  useEffect(() => {
    document.documentElement.lang = lang
    document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr'
    localStorage.setItem('tg.lang', lang)
  }, [lang])

  const allowed = user ? tabs.filter((x) => x.roles.includes(user.role)) : []
  useEffect(() => {
    if (allowed.length && !allowed.some((x) => x.id === tab)) setTab(allowed[0].id)
  }, [user]) // eslint-disable-line react-hooks/exhaustive-deps

  const logout = async () => {
    await api.post('/api/auth/logout')
    setUser(null)
  }

  return (
    <I18nContext.Provider value={i18n}>
      <header className="top">
        <strong>{i18n.t('appName')}</strong>
        {user && (
          <nav>
            {allowed.map((x) => (
              <button key={x.id} className={x.id === tab ? 'tab active' : 'tab'} onClick={() => setTab(x.id)}>
                {i18n.t(x.label)}
              </button>
            ))}
          </nav>
        )}
        <span className="spacer" />
        <button className="link" onClick={() => setLang(lang === 'ar' ? 'fr' : 'ar')}>{lang === 'ar' ? 'Français' : 'العربية'}</button>
        {user && (
          <>
            <span className="muted">{user.fullName} · {i18n.t(`r${user.role}` as Key)}</span>
            <button className="link" onClick={logout}>{i18n.t('signOut')}</button>
          </>
        )}
      </header>
      <main>
        {user === undefined ? null : user === null ? (
          <Login onLoggedIn={(u) => { setUser(u); setLang(u.language) }} />
        ) : tab === 'gate' ? <GatePage /> : tab === 'cameras' ? <CamerasPage /> : tab === 'phones' ? <PhonesPage />
          : tab === 'fleet' ? <FleetPage /> : tab === 'users' ? <UsersPage /> : <IntegrityPage />}
      </main>
    </I18nContext.Provider>
  )
}
