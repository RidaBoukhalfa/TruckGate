import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, ApiError, type Dashboard, type Role, type User } from './api'
import { I18nContext, makeI18n, type Key, type Lang } from './i18n'
import Login from './pages/Login'
import GatePage from './pages/Gate'
import CamerasPage from './pages/Cameras'
import PhonesPage from './pages/Phones'
import TrucksPage from './pages/Trucks'
import UsersPage from './pages/Users'
import IntegrityPage from './pages/Integrity'
import DashboardPage from './pages/Dashboard'
import ReviewPage from './pages/Review'
import AlertsPage from './pages/Alerts'
import PayPage from './pages/Pay'
import SettingsPage from './pages/Settings'
import ReceiptsPage from './pages/Receipts'

export type Tab = 'gate' | 'dashboard' | 'receipts' | 'review' | 'alerts' | 'trucks' | 'cameras' | 'phones' | 'pay' | 'users'
  | 'settings' | 'integrity'

interface NavItem { id: Tab; label: Key; roles: Role[] }
const groups: { title: Key; items: NavItem[] }[] = [
  {
    title: 'navOperations', items: [
      { id: 'gate', label: 'tabGate', roles: ['GATEKEEPER', 'SUPERVISOR', 'ADMIN'] },
      { id: 'dashboard', label: 'tabDashboard', roles: ['MANAGER', 'SUPERVISOR', 'ADMIN', 'ACCOUNTANT', 'AUDITOR'] },
      { id: 'receipts', label: 'tabReceipts', roles: ['SUPERVISOR', 'ADMIN', 'MANAGER', 'ACCOUNTANT', 'AUDITOR'] },
      { id: 'review', label: 'tabReview', roles: ['SUPERVISOR', 'ADMIN'] },
      { id: 'alerts', label: 'tabAlerts', roles: ['SUPERVISOR', 'ADMIN', 'MANAGER'] },
    ],
  },
  {
    title: 'navManagement', items: [
      { id: 'trucks', label: 'tabTrucks', roles: ['SUPERVISOR', 'ADMIN'] },
      { id: 'cameras', label: 'tabCameras', roles: ['SUPERVISOR', 'ADMIN'] },
      { id: 'phones', label: 'tabPhones', roles: ['ADMIN'] },
    ],
  },
  { title: 'navFinance', items: [{ id: 'pay', label: 'tabPay', roles: ['ACCOUNTANT', 'ADMIN', 'MANAGER'] }] },
  {
    title: 'navSystem', items: [
      { id: 'settings', label: 'tabSettings', roles: ['SUPERVISOR', 'ADMIN'] },
      { id: 'users', label: 'tabUsers', roles: ['ADMIN'] },
      { id: 'integrity', label: 'tabIntegrity', roles: ['ADMIN', 'MANAGER', 'AUDITOR'] },
    ],
  },
]
const allItems = groups.flatMap((g) => g.items)
const countRoles: Role[] = ['SUPERVISOR', 'ADMIN', 'MANAGER', 'ACCOUNTANT', 'AUDITOR']

function Clock({ lang }: { lang: Lang }) {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 15_000)
    return () => clearInterval(timer)
  }, [])
  return (
    <span className="clock">
      {now.toLocaleDateString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { weekday: 'short', day: '2-digit', month: '2-digit' })}
      {' · '}
      {now.toLocaleTimeString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { hour: '2-digit', minute: '2-digit' })}
    </span>
  )
}

export default function App() {
  const [user, setUser] = useState<User | null | undefined>(undefined)
  const [lang, setLang] = useState<Lang>(() => (localStorage.getItem('tg.lang') as Lang) || 'ar')
  const [tab, setTab] = useState<Tab>(() => (localStorage.getItem('tg.tab') as Tab) || 'gate')
  const [counts, setCounts] = useState<Dashboard | null>(null)
  const i18n = useMemo(() => makeI18n(lang), [lang])
  const { t } = i18n

  useEffect(() => {
    api.get<User>('/api/auth/me')
      .then((u) => { setUser(u); setLang(u.language) })
      .catch((e) => { if (e instanceof ApiError) setUser(null); else setUser(null) })
  }, [])

  useEffect(() => {
    document.documentElement.lang = lang
    document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr'
    localStorage.setItem('tg.lang', lang)
  }, [lang])
  useEffect(() => { localStorage.setItem('tg.tab', tab) }, [tab])

  const allowed = useMemo(() => (user ? allItems.filter((x) => x.roles.includes(user.role)) : []), [user])
  useEffect(() => {
    if (allowed.length && !allowed.some((x) => x.id === tab)) setTab(allowed[0].id)
  }, [allowed, tab])

  // Waiting work shown next to the menu entries (pending trucks, open reviews, open alerts).
  const loadCounts = useCallback(() => {
    if (!user || !countRoles.includes(user.role)) return
    api.get<Dashboard>('/api/reports/dashboard').then(setCounts).catch(() => undefined)
  }, [user])
  useEffect(() => {
    loadCounts()
    const timer = setInterval(loadCounts, 30_000)
    return () => clearInterval(timer)
  }, [loadCounts])

  const logout = async () => {
    await api.post('/api/auth/logout').catch(() => undefined)
    setUser(null)
    setCounts(null)
  }

  const badge = (id: Tab) => {
    if (!counts) return null
    const n = id === 'trucks' ? counts.trucksAwaitingApproval
      : id === 'review' ? counts.openReviews
        : id === 'alerts' ? counts.openAlertsCritical + counts.openAlertsOther : 0
    if (!n) return null
    const bad = id === 'alerts' && counts.openAlertsCritical > 0
    return <span className={`nav-count ${bad ? 'bad' : ''}`}>{n > 99 ? '99+' : n}</span>
  }

  const go = (id: Tab) => { setTab(id); loadCounts() }
  const langSwitch = (
    <button className="link" onClick={() => setLang(lang === 'ar' ? 'fr' : 'ar')}>{lang === 'ar' ? 'Français' : 'العربية'}</button>
  )

  if (user === undefined) return null
  if (user === null) {
    return (
      <I18nContext.Provider value={i18n}>
        <div className="login-page">
          <div className="login-lang">{langSwitch}</div>
          <Login onLoggedIn={(u) => { setUser(u); setLang(u.language) }} />
        </div>
      </I18nContext.Provider>
    )
  }

  const current = allItems.find((x) => x.id === tab)
  const page: Record<Tab, ReactNode> = {
    gate: <GatePage />, dashboard: <DashboardPage onOpen={go} canOpen={(x) => allowed.some((a) => a.id === x)} />, receipts: <ReceiptsPage role={user.role} />,
    review: <ReviewPage onChanged={loadCounts} />, alerts: <AlertsPage onChanged={loadCounts} />,
    trucks: <TrucksPage onChanged={loadCounts} />, cameras: <CamerasPage role={user.role} />, phones: <PhonesPage />,
    pay: <PayPage role={user.role} />, users: <UsersPage />, settings: <SettingsPage role={user.role} />,
    integrity: <IntegrityPage />,
  }

  return (
    <I18nContext.Provider value={i18n}>
      <div className="shell">
        <aside className="side">
          <div className="brand">
            <div className="brand-mark">TG</div>
            <div>
              <div className="brand-name">TruckGate</div>
              <div className="brand-sub">{t('appTagline')}</div>
            </div>
          </div>
          <nav>
            {groups.map((g) => {
              const items = g.items.filter((x) => x.roles.includes(user.role))
              if (!items.length) return null
              return (
                <div key={g.title} className="nav-group">
                  <div className="nav-title">{t(g.title)}</div>
                  {items.map((x) => (
                    <button key={x.id} className={`nav-item ${x.id === tab ? 'active' : ''}`} onClick={() => go(x.id)}
                      aria-current={x.id === tab ? 'page' : undefined}>
                      <span>{t(x.label)}</span>
                      {badge(x.id)}
                    </button>
                  ))}
                </div>
              )
            })}
          </nav>
          <div className="side-foot">
            <strong>{user.fullName}</strong>
            {t(`r${user.role}` as Key)}
            <div className="row-between">
              <button onClick={logout}>{t('signOut')}</button>
            </div>
          </div>
        </aside>
        <div className="workspace">
          <header className="topbar">
            <h1>{current ? t(current.label) : ''}</h1>
            <span className="spacer" />
            <Clock lang={lang} />
            {langSwitch}
          </header>
          <main>{page[tab]}</main>
        </div>
      </div>
    </I18nContext.Provider>
  )
}
