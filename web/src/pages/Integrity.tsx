import { useState } from 'react'
import { api, type Anchor, type AuditCheck, type Gap } from '../api'
import { useI18n } from '../i18n'
import { ErrorBox, Section, fmtTime, useAction } from '../ui'

/** Recomputes the audit chain, records an anchor to keep outside the factory, lists missing ticket numbers. */
export default function IntegrityPage() {
  const { t, lang } = useI18n()
  const [check, setCheck] = useState<AuditCheck | null>(null)
  const [anchor, setAnchor] = useState<Anchor | null>(null)
  const [gaps, setGaps] = useState<Gap[] | null>(null)
  const act = useAction()

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      <Section title={t('integrity')}>
        <div className="row">
          <button className="primary" disabled={act.busy} onClick={() => act.run(async () => setCheck(await api.get<AuditCheck>('/api/audit/verify')))}>
            {t('verify')}
          </button>
          <button disabled={act.busy} onClick={() => act.run(async () => setAnchor(await api.post<Anchor>('/api/audit/anchor')))}>
            {t('anchor')}
          </button>
          <button disabled={act.busy} onClick={() => act.run(async () => setGaps(await api.get<Gap[]>('/api/audit/sequence-gaps')))}>
            {t('gaps')}
          </button>
        </div>
        {check && (
          <div className={`banner ${check.ok ? 'accept' : 'reject'}`}>
            {check.ok ? t('chainOk', { n: check.checked }) : t('chainBad', { id: check.firstBadId ?? '?', problem: check.problem ?? '' })}
          </div>
        )}
        {anchor && (
          <div className="card">
            <p>{t('anchorNote')}</p>
            <code className="hash">#{anchor.auditId} · {anchor.hash}</code>
            <p className="muted">{fmtTime(anchor.createdAt, lang)}</p>
          </div>
        )}
        {gaps && (gaps.length === 0 ? <p className="ok">{t('noGaps')}</p> : (
          <ul>{gaps.map((g) => <li key={`${g.deviceId}-${g.gapFrom}`}>Q{g.deviceId}: {g.gapFrom} → {g.gapTo}</li>)}</ul>
        ))}
      </Section>
    </div>
  )
}
