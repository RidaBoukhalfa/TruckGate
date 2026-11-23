import { useCallback, useEffect, useState } from 'react'
import { api, type BurnedPage, type BurnedReceipt, type Lifecycle, type Role, type VideoStatus } from '../api'
import TripView from '../components/TripView'
import { useI18n, type Key } from '../i18n'
import { ErrorBox, fmtTime, useAction } from '../ui'

const lifecycles: Lifecycle[] = ['SCANNED_AT_ENTRY', 'ENTRY_REVIEW', 'INSIDE_FACTORY', 'SCANNED_AT_EXIT', 'CLOSED', 'REJECTED']
const lifecycleTone: Record<Lifecycle, string> = {
  SCANNED_AT_ENTRY: 'info', ENTRY_REVIEW: 'warn', INSIDE_FACTORY: 'info', SCANNED_AT_EXIT: 'info', CLOSED: 'ok', REJECTED: 'bad',
}
const reviewStates = ['OPEN', 'INVESTIGATING', 'DECIDED', 'NONE'] as const
const pageSize = 100
const today = () => new Date().toISOString().slice(0, 10)
const daysAgo = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString().slice(0, 10)
const evidenceRoles: Role[] = ['SUPERVISOR', 'ADMIN', 'MANAGER', 'AUDITOR']

function VideoMark({ s }: { s: VideoStatus | null }) {
  const { t } = useI18n()
  if (!s) return <span className="muted">—</span>
  const tone = s === 'OK' ? 'ok' : s === 'FAILED' ? 'bad' : s === 'PARTIAL' || s === 'RECORDING' ? 'warn' : ''
  return <span className={`badge ${tone}`}>{t(`v${s}` as Key)}</span>
}

/**
 * Burned receipts: every quarry ticket that was used at the gate, with its whole life (issued, scanned,
 * inside, exit, closed or rejected), photo, videos, review and payment. Nothing is ever deleted here.
 */
export default function ReceiptsPage({ role }: { role: Role }) {
  const { t, lang } = useI18n()
  const [filter, setFilter] = useState({ from: daysAgo(7), to: today(), lifecycle: '', review: '', search: '' })
  const [applied, setApplied] = useState(filter)
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState<BurnedPage | null>(null)
  const [open, setOpen] = useState<BurnedReceipt | null>(null)
  const act = useAction()
  const canOpen = evidenceRoles.includes(role)
  const editable = role === 'SUPERVISOR' || role === 'ADMIN'

  const load = useCallback(async () => {
    const q = new URLSearchParams({ from: applied.from, to: applied.to, limit: String(pageSize), offset: String(offset) })
    if (applied.lifecycle) q.set('lifecycle', applied.lifecycle)
    if (applied.review) q.set('review', applied.review)
    if (applied.search.trim()) q.set('search', applied.search.trim())
    setPage(await api.get<BurnedPage>(`/api/receipts/burned?${q}`))
  }, [applied, offset])
  useEffect(() => { act.run(load) }, [load]) // eslint-disable-line react-hooks/exhaustive-deps

  const apply = () => { setOffset(0); setApplied({ ...filter }) }
  const set = (k: keyof typeof filter, v: string) => setFilter((f) => ({ ...f, [k]: v }))

  return (
    <div className="stack">
      <p className="muted">{t('receiptsHelp')}</p>
      <section className="card flush">
        <form className="filters" onSubmit={(e) => { e.preventDefault(); apply() }}>
          <label>{t('from')}<input type="date" value={filter.from} onChange={(e) => set('from', e.target.value)} /></label>
          <label>{t('to')}<input type="date" value={filter.to} onChange={(e) => set('to', e.target.value)} /></label>
          <label>{t('lifecycle')}
            <select value={filter.lifecycle} onChange={(e) => set('lifecycle', e.target.value)}>
              <option value="">{t('all')}</option>
              {lifecycles.map((l) => <option key={l} value={l}>{t(`lc${l}` as Key)}</option>)}
            </select>
          </label>
          <label>{t('reviewStatus')}
            <select value={filter.review} onChange={(e) => set('review', e.target.value)}>
              <option value="">{t('all')}</option>
              {reviewStates.filter((r) => r !== 'NONE').map((r) => <option key={r} value={r}>{t(`rv${r}` as Key)}</option>)}
            </select>
          </label>
          <label className="grow">{t('searchReceipts')}
            <input value={filter.search} onChange={(e) => set('search', e.target.value)} placeholder="Q1-000123 · T-000045 · 12" />
          </label>
          <button className="primary" disabled={act.busy}>{t('show')}</button>
        </form>
        <ErrorBox error={act.error} />
        {page === null && <div className="loading">{t('loading')}</div>}
        {page?.items.length === 0 && <div className="empty"><strong>{t('noReceipts')}</strong></div>}
        {page && page.items.length > 0 && (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>{t('ticket')}</th><th>{t('trip')}</th><th>{t('truck')}</th><th>{t('driver')}</th><th>{t('operator')}</th>
                  <th>{t('receiptCreated')}</th><th>{t('entered')}</th><th>{t('exitedAt')}</th><th>{t('lifecycle')}</th>
                  <th>{t('reviewStatus')}</th><th>{t('entryVideo')}</th><th>{t('exitVideo')}</th><th>{t('cargoPhoto')}</th>
                </tr>
              </thead>
              <tbody>
                {page.items.map((r) => (
                  <tr key={r.tripId} className={canOpen ? 'clickable' : ''} onClick={() => canOpen && setOpen(r)}>
                    <td className="code">{r.ticketId}</td>
                    <td className="code">{r.tripNumber}</td>
                    <td>
                      <strong>{r.fleetNumber ?? r.truckId}</strong> <span className="code small muted">{r.plate ?? ''}</span>
                      {r.truckStatus && r.truckStatus !== 'APPROVED' && <div><span className="badge amber">{t(`ts${r.truckStatus}` as Key)}</span></div>}
                    </td>
                    <td><bdi>{r.driverName ?? '—'}</bdi></td>
                    <td><bdi>{r.operatorName ?? '—'}</bdi></td>
                    <td className="small">{fmtTime(r.issuedAt, lang)}</td>
                    <td className="small">{fmtTime(r.enteredAt, lang)}</td>
                    <td className="small">{fmtTime(r.exitedAt, lang)}</td>
                    <td>
                      <span className={`badge ${lifecycleTone[r.lifecycle]}`}>{t(`lc${r.lifecycle}` as Key)}</span>
                      {r.paid && <div><span className="badge ok">{t('paidBadge')}</span></div>}
                      {r.voidedAfterUse && <div><span className="badge bad">{t('voidedAfter')}</span></div>}
                    </td>
                    <td>{r.reviewStatus === 'NONE' ? <span className="muted">—</span>
                      : <span className={`badge ${r.reviewStatus === 'DECIDED' ? '' : 'warn'}`}>{t(`rv${r.reviewStatus}` as Key)}</span>}</td>
                    <td><VideoMark s={r.entryVideo} /></td>
                    <td><VideoMark s={r.exitVideo} /></td>
                    <td className="small">{r.hasPhoto ? <span className="badge ok">✓</span> : r.photoExpected
                      ? <span className="badge warn" title={t('photoNotSynced')}>{t('photoPendingShort')}</span>
                      : <span className="badge" title={t('photoNone')}>{t('photoNoneShort')}</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {page && (
          <div className="pager">
            <span className="muted small">{t('resultsN', { n: page.total })}{page.total > pageSize ? ` · ${offset + 1}–${Math.min(offset + pageSize, page.total)}` : ''}</span>
            <button disabled={offset === 0 || act.busy} onClick={() => setOffset(Math.max(0, offset - pageSize))}>{t('prevPage')}</button>
            <button disabled={offset + pageSize >= page.total || act.busy} onClick={() => setOffset(offset + pageSize)}>{t('nextPage')}</button>
          </div>
        )}
      </section>

      {open && (
        <>
          <div className="drawer-back" onClick={() => setOpen(null)} />
          <aside className="drawer" role="dialog" aria-label={open.tripNumber}>
            <div className="drawer-head">
              <h2><span className="code">{open.ticketId}</span> · {open.tripNumber}</h2>
              <button onClick={() => setOpen(null)}>{t('close2')}</button>
            </div>
            <TripView tripId={open.tripId} editable={editable} onChanged={() => { load().catch(() => undefined) }} />
          </aside>
        </>
      )}
    </div>
  )
}
