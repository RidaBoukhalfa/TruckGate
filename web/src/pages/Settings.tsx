import { useEffect, useState } from 'react'
import { api, type ChannelStatus, type SettingItem } from '../api'
import { useI18n } from '../i18n'
import { ErrorBox, Section, useAction } from '../ui'

// Labels per setting key, Arabic then French.
const labels: Record<string, [string, string]> = {
  'gate.min_travel_s': ['أقل عمر للتذكرة عند البوابة (ثانية)', 'Âge minimum du bon au portail (s)'],
  'gate.max_validity_s': ['أقصى صلاحية للتذكرة (ثانية)', 'Validité maximale du bon (s)'],
  'gate.min_truck_gap_s': ['أقل مدة بين دخولين لنفس الشاحنة (ثانية)', 'Écart minimum entre deux entrées du camion (s)'],
  'gate.future_tolerance_s': ['تسامح ساعة الهاتف للأمام (ثانية)', "Tolérance d'horloge en avance (s)"],
  'gate.min_unload_s': ['أقل مدة للتفريغ قبل الخروج (ثانية)', 'Durée minimale de déchargement (s)'],
  'gate.entry_decision_alert_s': ['تنبيه إذا بقي الدخول بلا قرار (ثانية)', 'Alerte si entrée sans décision (s)'],
  'trip.exit_deadline_s': ['تنبيه إذا لم تخرج الشاحنة بعد (ثانية)', 'Alerte si camion non sorti après (s)'],
  'trip.price_per_trip': ['سعر الرحلة', 'Prix par voyage'],
  'trip.partial_pay_percent': ['نسبة دفع الرحلة الناقصة (%)', 'Taux payé pour un voyage partiel (%)'],
  'trip.currency': ['العملة', 'Devise'],
  'trip.partial_alert_threshold': ['تنبيه بعد عدد حمولات ناقصة', 'Alerte après N chargements partiels'],
  'trip.partial_alert_window_days': ['خلال عدد أيام', 'Sur N jours'],
  'review.sample_percent': ['نسبة العينة اليومية للمراجعة (%)', 'Échantillon quotidien à vérifier (%)'],
  'site.timezone': ['المنطقة الزمنية', 'Fuseau horaire'],
  'quarry.truck_cooldown_s': ['المقلع: أقل مدة بين تذكرتين لنفس الشاحنة (ثانية)', 'Carrière : écart entre deux bons du camion (s)'],
  'quarry.clock_mismatch_s': ['المقلع: فرق ساعة الهاتف المقبول (ثانية)', "Carrière : écart d'horloge accepté (s)"],
  'quarry.sync_max_age_s': ['المقلع: أقصى مدة بلا مزامنة (ثانية)', 'Carrière : durée maximale sans synchro (s)'],
  'quarry.gps_max_age_s': ['المقلع: أقصى عمر لموقع GPS (ثانية)', 'Carrière : âge maximal du GPS (s)'],
  'quarry.gps_max_accuracy_m': ['المقلع: أسوأ دقة GPS مقبولة (متر)', 'Carrière : précision GPS maximale (m)'],
  'quarry.max_unsynced_tickets': ['المقلع: أقصى تذاكر غير مزامنة', 'Carrière : bons non synchronisés max.'],
  'auth.max_failed_logins': ['محاولات دخول خاطئة قبل القفل', 'Échecs de connexion avant blocage'],
  'auth.lockout_s': ['مدة القفل (ثانية)', 'Durée du blocage (s)'],
  'alerts.language': ['لغة التنبيهات (ar / fr)', 'Langue des alertes (ar / fr)'],
  'alerts.sms_enabled': ['تفعيل SMS', 'Activer les SMS'],
  'alerts.sms_port': ['منفذ مودم GSM (COM3 أو /dev/ttyUSB0)', 'Port du modem GSM (COM3 ou /dev/ttyUSB0)'],
  'alerts.sms_recipients': ['أرقام SMS (مفصولة بفاصلة)', 'Numéros SMS (séparés par des virgules)'],
  'alerts.sms_min_severity': ['أدنى خطورة لإرسال SMS', 'Gravité minimale pour SMS'],
  'alerts.sms_max_per_hour': ['أقصى عدد SMS في الساعة', 'SMS maximum par heure'],
  'alerts.telegram_enabled': ['تفعيل Telegram', 'Activer Telegram'],
  'alerts.telegram_chat_ids': ['معرّفات محادثات Telegram (مفصولة بفاصلة)', 'Identifiants de discussions Telegram (virgules)'],
  'alerts.telegram_min_severity': ['أدنى خطورة لإرسال Telegram', 'Gravité minimale pour Telegram'],
  'alerts.telegram_photos': ['إرفاق صورة الكاميرا في Telegram', 'Joindre la photo dans Telegram'],
}

function toInput(s: SettingItem): string {
  try {
    const v = JSON.parse(s.value)
    return Array.isArray(v) ? v.join(', ') : String(v)
  } catch {
    return s.value
  }
}

function fromInput(s: SettingItem, text: string): unknown {
  switch (s.type) {
    case 'int': case 'decimal': return Number(text)
    case 'bool': return text === 'true'
    case 'phones': case 'chats': return text.split(/[,\s]+/).map((x) => x.trim()).filter(Boolean)
    default: return text
  }
}

/** Administrator: business parameters and alert channels. Every change is recorded in the audit chain. */
export default function SettingsPage() {
  const { t, lang } = useI18n()
  const [items, setItems] = useState<SettingItem[]>([])
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [channels, setChannels] = useState<ChannelStatus | null>(null)
  const [token, setToken] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const act = useAction()

  const load = async () => {
    setItems(await api.get<SettingItem[]>('/api/admin/settings'))
    setChannels(await api.get<ChannelStatus>('/api/admin/channels'))
  }
  useEffect(() => { act.run(load) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const save = (s: SettingItem) => act.run(async () => {
    await api.put(`/api/admin/settings/${encodeURIComponent(s.key)}`, { value: fromInput(s, edits[s.key] ?? toInput(s)) })
    setEdits((e) => { const n = { ...e }; delete n[s.key]; return n })
    setNotice(t('saved'))
    await load()
  })
  const test = (channel: 'SMS' | 'TELEGRAM') => act.run(async () => {
    const r = await api.post<{ errors: string[] }>('/api/admin/channels/test', { channel })
    setNotice(r.errors.length ? r.errors.join(' · ') : t('testOk'))
  })

  const groups = items.reduce<Record<string, SettingItem[]>>((g, s) => {
    const k = s.key.split('.')[0]
    ;(g[k] ??= []).push(s)
    return g
  }, {})

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      {notice && <div className="card ok" onClick={() => setNotice(null)}>{notice}</div>}
      <Section title={t('alertChannels')}>
        {channels && (
          <p className="small muted">
            SMS: {channels.smsEnabled ? '✓' : '✗'} {channels.smsPort} → {channels.smsRecipients.join(', ') || '—'} ·
            Telegram: {channels.telegramEnabled ? '✓' : '✗'} {channels.telegramTokenSet ? t('tokenSet') : ''} → {channels.telegramChatIds.join(', ') || '—'}
          </p>
        )}
        <div className="row">
          <label>{t('telegramToken')}<input type="password" className="wide" value={token} onChange={(e) => setToken(e.target.value)} placeholder="123456:ABC…" /></label>
          <button disabled={!token} onClick={() => act.run(async () => {
            await api.put('/api/admin/channels/telegram-token', { token }); setToken(''); setNotice(t('saved')); await load()
          })}>{t('save')}</button>
          <button onClick={() => test('SMS')} disabled={act.busy}>{t('testSms')}</button>
          <button onClick={() => test('TELEGRAM')} disabled={act.busy}>{t('testTelegram')}</button>
        </div>
      </Section>
      {Object.entries(groups).map(([g, list]) => (
        <Section key={g} title={g}>
          <table>
            <tbody>
              {list.map((s) => {
                const value = edits[s.key] ?? toInput(s)
                const changed = edits[s.key] !== undefined && edits[s.key] !== toInput(s)
                return (
                  <tr key={s.key}>
                    <td>{labels[s.key]?.[lang === 'ar' ? 0 : 1] ?? s.key}<div className="muted small ltr">{s.key}</div></td>
                    <td>
                      {s.type === 'bool' ? (
                        <select value={value} onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })}>
                          <option value="true">{t('yes')}</option><option value="false">{t('no')}</option>
                        </select>
                      ) : s.type === 'enum' ? (
                        <select value={value} onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })}>
                          {(s.min ?? '').split(',').map((o) => <option key={o} value={o}>{o}</option>)}
                        </select>
                      ) : (
                        <input value={value} className={s.type === 'phones' || s.type === 'chats' ? 'wide' : ''}
                          inputMode={s.type === 'int' || s.type === 'decimal' ? 'decimal' : undefined}
                          onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })} />
                      )}
                      {(s.type === 'int' || s.type === 'decimal') && <span className="muted small"> {s.min}–{s.max}</span>}
                    </td>
                    <td><button className={changed ? 'primary' : ''} disabled={!changed || act.busy} onClick={() => save(s)}>{t('save')}</button></td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </Section>
      ))}
    </div>
  )
}
