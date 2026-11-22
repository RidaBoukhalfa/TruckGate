import { useState, type FormEvent } from 'react'
import { api, type User } from '../api'
import { useI18n } from '../i18n'
import { ErrorBox, useAction } from '../ui'

export default function Login({ onLoggedIn }: { onLoggedIn: (u: User) => void }) {
  const { t } = useI18n()
  const [username, setUsername] = useState('')
  const [secret, setSecret] = useState('')
  const { busy, error, run } = useAction()

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const u = await run(() => api.post<User>('/api/auth/login', { username, secret }))
    setSecret('')
    if (u) onLoggedIn(u)
  }

  return (
    <form className="card login" onSubmit={submit}>
      <h2>{t('login')}</h2>
      <label>{t('username')}<input autoFocus value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" /></label>
      <label>{t('secret')}<input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="current-password" /></label>
      <ErrorBox error={error} />
      <button className="primary" disabled={busy || !username || !secret}>{t('signIn')}</button>
    </form>
  )
}
