import { useState, type ReactNode } from 'react'
import { ApiError } from './api'
import { useI18n } from './i18n'

/** Runs an async action, keeps a busy flag and a translated error message. */
export function useAction() {
  const { reason } = useI18n()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const run = async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true)
    setError(null)
    try {
      return await fn()
    } catch (e) {
      setError(e instanceof ApiError ? `${reason(e.code)}${e.message && reason(e.code) !== e.message ? ' — ' + e.message : ''}` : String(e))
      return undefined
    } finally {
      setBusy(false)
    }
  }
  return { busy, error, setError, run }
}

export function ErrorBox({ error }: { error: string | null }) {
  return error ? <div className="error">{error}</div> : null
}

export function Section({ title, children, actions }: { title: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="card">
      <div className="card-head">
        <h2>{title}</h2>
        {actions}
      </div>
      {children}
    </section>
  )
}

export const fmtTime = (iso: string | null, lang: string) =>
  iso ? new Date(iso).toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { dateStyle: 'short', timeStyle: 'short' }) : '—'
