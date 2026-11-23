import { useEffect, useState } from 'react'
import { api, type ChannelStatus, type Role, type SettingItem } from '../api'
import { useI18n, type Key } from '../i18n'
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
  'alerts.sms_transport': ['طريقة إرسال SMS (MODEM مودم USB، ZTE_ROUTER راوتر ZTE)', "Envoi des SMS (MODEM USB ou ZTE_ROUTER)"],
  'alerts.sms_router_url': ['عنوان واجهة الراوتر (مثلاً http://192.168.0.1)', 'Adresse du routeur (ex. http://192.168.0.1)'],
  'alerts.sms_router_user': ['اسم مستخدم الراوتر (فارغ إذا لا يطلبه)', 'Utilisateur du routeur (vide si non demandé)'],
  'alerts.whatsapp_enabled': ['تفعيل WhatsApp', 'Activer WhatsApp'],
  'alerts.whatsapp_phone_number_id': ['معرّف رقم الهاتف (Phone number ID من Meta)', 'Identifiant du numéro (Phone number ID Meta)'],
  'alerts.whatsapp_recipients': ['أرقام WhatsApp المستلمة (مفصولة بفاصلة)', 'Numéros WhatsApp destinataires (virgules)'],
  'alerts.whatsapp_template': ['اسم القالب المعتمد', 'Nom du modèle approuvé'],
  'alerts.whatsapp_template_language': ['لغة القالب (ar أو fr)', 'Langue du modèle (ar ou fr)'],
  'alerts.whatsapp_min_severity': ['أدنى خطورة لإرسال WhatsApp', 'Gravité minimale pour WhatsApp'],
  'alerts.whatsapp_photos': ['إرفاق صورة (القالب يجب أن يحتوي رأس صورة)', 'Joindre la photo (modèle avec en-tête image)'],
  'alerts.whatsapp_api_version': ['نسخة Graph API', 'Version Graph API'],
  'site.factory_name': ['اسم المصنع (يُطبع على وصل المقلع)', "Nom de l'usine (imprimé sur le bon de carrière)"],
  'quarry.photo_required': ['فرض تصوير الحمولة (لا يصدر الوصل بدون صورة)', 'Photo du chargement obligatoire (pas de bon sans photo)'],
  'quarry.sync_overdue_policy': ['بعد 24 ساعة بلا مزامنة: BLOCK يمنع إصدار وصلات جديدة، WARN تحذير فقط', 'Après 24 h sans synchro : BLOCK bloque les nouveaux bons, WARN avertit seulement'],
  'video.pre_s': ['مدة الفيديو قبل مسح الوصل (ثانية)', 'Vidéo avant le scan (s)'],
  'video.post_s': ['مدة الفيديو بعد مسح الوصل (ثانية)', 'Vidéo après le scan (s)'],
  'video.segment_s': ['طول مقاطع التسجيل المستمر (ثانية)', "Durée des segments d'enregistrement (s)"],
  'video.retention_days': ['مدة حفظ الفيديو (يوم)', 'Conservation des vidéos (jours)'],
  'video.keep_reviewed': ['الاحتفاظ بفيديو الرحلات التي فيها مراجعة', 'Garder les vidéos des voyages vérifiés'],
  'video.failure_opens_review': ['فشل تسجيل الفيديو يفتح مراجعة', "Un échec vidéo ouvre une vérification"],
  'storage.min_free_gb': ['تنبيه إذا قلّت المساحة الحرة عن (GB)', 'Alerte si espace libre sous (Go)'],
  'pay.require_exit_scan': ['الدفع فقط للرحلات التي سُجّل خروجها', 'Payer uniquement les voyages avec sortie enregistrée'],
}

const groupOf = (key: string): Key => {
  const p = key.split('.')[0]
  return p === 'quarry' ? 'sgQuarry' : p === 'gate' ? 'sgGate' : p === 'video' || p === 'storage' ? 'sgVideo'
    : p === 'trip' || p === 'pay' ? 'sgFinance' : p === 'review' ? 'sgReview' : p === 'site' ? 'sgSite'
      : p === 'auth' ? 'sgAuth' : 'sgAlerts'
}
const groupOrder: Key[] = ['sgQuarry', 'sgGate', 'sgVideo', 'sgFinance', 'sgReview', 'sgSite', 'sgAuth', 'sgAlerts']

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
    case 'phones': case 'chats': return text.split(/[,;\n]+/).map((x) => x.trim()).filter(Boolean)
    default: return text
  }
}

/**
 * Supervisor and administrator: operating parameters (quarry phones, gate, video, payment). Alert channels,
 * sign-in rules and the time zone stay with the administrator. Every change is recorded in the audit chain.
 */
export default function SettingsPage({ role }: { role: Role }) {
  const isAdmin = role === 'ADMIN'
  const { t, lang } = useI18n()
  const [items, setItems] = useState<SettingItem[]>([])
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [channels, setChannels] = useState<ChannelStatus | null>(null)
  const [token, setToken] = useState('')
  const [waToken, setWaToken] = useState('')
  const [routerPass, setRouterPass] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const act = useAction()

  const load = async () => {
    setItems(await api.get<SettingItem[]>('/api/settings'))
    if (isAdmin) setChannels(await api.get<ChannelStatus>('/api/admin/channels'))
  }
  useEffect(() => { act.run(load) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const save = (s: SettingItem) => act.run(async () => {
    await api.put(`/api/settings/${encodeURIComponent(s.key)}`, { value: fromInput(s, edits[s.key] ?? toInput(s)) })
    setEdits((e) => { const n = { ...e }; delete n[s.key]; return n })
    setNotice(t('saved'))
    await load()
  })
  const saveSecret = (key: string, value: string, reset: () => void) => act.run(async () => {
    await api.put(`/api/admin/channels/secrets/${encodeURIComponent(key)}`, { token: value })
    reset()
    setNotice(t('saved'))
    await load()
  })
  const test = (channel: 'SMS' | 'TELEGRAM' | 'WHATSAPP') => act.run(async () => {
    const r = await api.post<{ errors: string[] }>('/api/admin/channels/test', { channel })
    setNotice(r.errors.length ? r.errors.join(' · ') : t('testOk'))
  })

  const groups = items.reduce<Partial<Record<Key, SettingItem[]>>>((g, s) => {
    ;(g[groupOf(s.key)] ??= []).push(s)
    return g
  }, {})

  return (
    <div className="stack">
      <ErrorBox error={act.error} />
      {notice && <div className="notice" onClick={() => setNotice(null)}>{notice}</div>}
      {groupOrder.filter((g) => groups[g]?.length).map((g) => (
        <Section key={g} title={t(g)}>
          <table>
            <tbody>
              {groups[g]!.map((s) => {
                const value = edits[s.key] ?? toInput(s)
                const changed = edits[s.key] !== undefined && edits[s.key] !== toInput(s)
                const locked = s.adminOnly && !isAdmin
                return (
                  <tr key={s.key} className={locked ? 'inactive' : ''}>
                    <td>{labels[s.key]?.[lang === 'ar' ? 0 : 1] ?? s.key}<div className="muted small ltr">{s.key}</div></td>
                    <td>
                      {s.type === 'bool' ? (
                        <select value={value} disabled={locked} onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })}>
                          <option value="true">{t('yes')}</option><option value="false">{t('no')}</option>
                        </select>
                      ) : s.type === 'enum' ? (
                        <select value={value} disabled={locked} onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })}>
                          {(s.min ?? '').split(',').map((o) => <option key={o} value={o}>{o}</option>)}
                        </select>
                      ) : (
                        <input value={value} disabled={locked} className={s.type === 'phones' || s.type === 'chats' || s.type === 'string' ? 'wide' : 'narrow-wide'}
                          inputMode={s.type === 'int' || s.type === 'decimal' ? 'decimal' : undefined}
                          onChange={(e) => setEdits({ ...edits, [s.key]: e.target.value })} />
                      )}
                      {(s.type === 'int' || s.type === 'decimal') && <span className="muted small"> <bdi>{s.min}–{s.max}</bdi></span>}
                    </td>
                    <td>{locked ? <span className="badge">{t('adminOnly')}</span>
                      : <button className={changed ? 'primary' : ''} disabled={!changed || act.busy} onClick={() => save(s)}>{t('save')}</button>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </Section>
      ))}
      {isAdmin && <Section title={t('alertChannels')}>
        {channels && (
          <ul className="small muted">
            <li>SMS: {channels.smsEnabled ? '✓' : '✗'} {channels.smsTransport === 'ZTE_ROUTER'
              ? `${channels.smsRouterUrl} ${channels.smsRouterPasswordSet ? '(' + t('secretSet') + ')' : ''}` : channels.smsPort}
              {' → '}{channels.smsRecipients.join(', ') || '—'}</li>
            <li>WhatsApp: {channels.whatsappEnabled ? '✓' : '✗'} {channels.whatsappPhoneNumberId}
              {channels.whatsappTokenSet ? ` (${t('secretSet')})` : ''} · {channels.whatsappTemplate}
              {' → '}{channels.whatsappRecipients.join(', ') || '—'}</li>
            <li>Telegram: {channels.telegramEnabled ? '✓' : '✗'} {channels.telegramTokenSet ? t('tokenSet') : ''}
              {' → '}{channels.telegramChatIds.join(', ') || '—'}</li>
          </ul>
        )}
        <div className="row">
          <label>{t('whatsappToken')}<input type="password" className="wide" value={waToken} onChange={(e) => setWaToken(e.target.value)} placeholder="EAAG…" /></label>
          <button disabled={!waToken} onClick={() => saveSecret('whatsapp.access_token', waToken, () => setWaToken(''))}>{t('save')}</button>
          <button onClick={() => test('WHATSAPP')} disabled={act.busy}>{t('testWhatsapp')}</button>
        </div>
        <div className="row">
          <label>{t('routerPassword')}<input type="password" value={routerPass} onChange={(e) => setRouterPass(e.target.value)} /></label>
          <button disabled={!routerPass} onClick={() => saveSecret('sms.router_password', routerPass, () => setRouterPass(''))}>{t('save')}</button>
          <button onClick={() => test('SMS')} disabled={act.busy}>{t('testSms')}</button>
        </div>
        <div className="row">
          <label>{t('telegramToken')}<input type="password" className="wide" value={token} onChange={(e) => setToken(e.target.value)} placeholder="123456:ABC…" /></label>
          <button disabled={!token} onClick={() => saveSecret('telegram.bot_token', token, () => setToken(''))}>{t('save')}</button>
          <button onClick={() => test('TELEGRAM')} disabled={act.busy}>{t('testTelegram')}</button>
        </div>
      </Section>}
    </div>
  )
}
