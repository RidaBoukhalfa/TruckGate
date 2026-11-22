import { createContext, useContext } from 'react'

export type Lang = 'ar' | 'fr'

const ar = {
  appName: 'TruckGate — بوابة المصنع',
  login: 'تسجيل الدخول', username: 'اسم المستخدم', secret: 'الرمز السري / كلمة المرور', signIn: 'دخول', signOut: 'خروج',
  tabGate: 'البوابة', tabCameras: 'الكاميرات', tabPhones: 'هواتف المقلع', tabFleet: 'الشاحنات والسائقون',
  tabUsers: 'المستخدمون', tabIntegrity: 'سلامة السجل',
  chooseGate: 'اختر البوابة', scanHere: 'امسح التذكرة هنا', scanning: 'جارٍ التحقق…',
  outcomeACCEPT: 'تذكرة صحيحة', outcomePENDING: 'تذكرة صحيحة — تحتاج مراجعة', outcomeREJECT: 'تذكرة مرفوضة',
  ticket: 'التذكرة', truck: 'الشاحنة', plate: 'اللوحة', driver: 'السائق', usualDriver: 'السائق المعتاد',
  otherDriver: 'سائق غير معتاد', operator: 'عامل المقلع', issued: 'صدرت', minutesAgo: 'منذ {n} دقيقة',
  pictures: 'صور الكاميرات', noPictures: 'لم تلتقط أي كاميرا صورة', decide: 'قرار البواب',
  dFULL: 'كاملة', dPARTIAL: 'ناقصة', dEMPTY: 'فارغة', dDIFFERENT_TRUCK: 'شاحنة مختلفة', dPLATE_UNREADABLE: 'لوحة غير مقروءة',
  stateENTERED: 'تم قبول الدخول', statePENDING_REVIEW: 'أُرسلت للمراجعة', nextTruck: 'الشاحنة التالية (Esc)',
  cameras: 'الكاميرات', discover: 'اكتشاف الكاميرات في الشبكة', discovering: 'جارٍ البحث…', found: 'أجهزة وُجدت',
  noneFound: 'لم يُعثر على أي جهاز. تأكد أن الخادم والكاميرات على نفس الشبكة، أو أضف الكاميرا يدوياً.',
  use: 'استعمال', onvifAddress: 'عنوان ONVIF', camUser: 'مستخدم الكاميرا', camPassword: 'كلمة مرور الكاميرا',
  connect: 'اتصال', channels: 'القنوات', preview: 'معاينة', add: 'إضافة', save: 'حفظ', label: 'الاسم',
  role: 'الدور', rolePLATE: 'لوحة الترقيم', roleCARGO: 'الحمولة', roleOVERVIEW: 'عامة', gate: 'البوابة',
  captureOnScan: 'تلتقط صوراً عند الحرق', frames: 'عدد الصور', intervalMs: 'الفاصل (مللي ثانية)',
  manualCamera: 'إضافة كاميرا بعنوان الصورة مباشرة', snapshotUrl: 'عنوان الصورة (snapshot)', test: 'تجربة',
  lastOk: 'آخر نجاح', lastError: 'آخر خطأ', active: 'نشط', version: 'نسخة', noSnapshot: 'هذه القناة لا تعطي صورة',
  gates: 'البوابات', newGate: 'بوابة جديدة', code: 'الرمز', name: 'الاسم', create: 'إنشاء',
  phones: 'هواتف المقلع', newCode: 'رمز تسجيل جديد', codeFor: 'اسم الهاتف', expires: 'ينتهي', scanWithPhone:
    'امسح هذا الرمز من تطبيق المقلع (على شبكة المصنع). يعمل مرة واحدة.', revoke: 'إلغاء الهاتف',
  revokeReason: 'سبب الإلغاء', revoked: 'ملغى', enrolled: 'سُجّل',
  sites: 'المواقع', quarry: 'مقلع', factory: 'مصنع', geofence: 'حدود المقلع (خط لكل نقطة: خط العرض، خط الطول)',
  saveGeofence: 'حفظ الحدود', hasGeofence: 'الحدود محددة',
  drivers: 'السائقون', trucks: 'الشاحنات', fullName: 'الاسم الكامل', phone: 'الهاتف', employee: 'أجير',
  contractor: 'مقاول', truckId: 'رقم الشاحنة (ثابت)', fleetNumber: 'رقم الأسطول', model: 'الطراز',
  defaultDriver: 'السائق المعتاد', changePlate: 'تغيير اللوحة', enable: 'تفعيل', disable: 'تعطيل',
  users: 'المستخدمون', language: 'اللغة', setSecret: 'تعيين الرمز', locked: 'مقفل', lastLogin: 'آخر دخول',
  rQUARRY_OPERATOR: 'عامل مقلع', rGATEKEEPER: 'بواب', rSUPERVISOR: 'مشرف', rACCOUNTANT: 'محاسب', rMANAGER: 'مدير',
  rADMIN: 'مسؤول النظام', rAUDITOR: 'مدقق',
  integrity: 'سلامة السجل', verify: 'فحص السلسلة', chainOk: 'السجل سليم: {n} حدثاً تم فحصه',
  chainBad: 'تلاعب مكتشف عند الحدث {id}: {problem}', anchor: 'إنشاء نقطة مرجع', anchorNote:
    'اكتب هذه البصمة أو أرسلها خارج المصنع: تكشف أي إعادة كتابة كاملة للسجل.', gaps: 'أرقام تذاكر مفقودة', noGaps: 'لا توجد فجوات',
  error: 'خطأ', close: 'إغلاق', yes: 'نعم', no: 'لا', none: '—',
} as const

export type Key = keyof typeof ar

const fr: Record<Key, string> = {
  appName: 'TruckGate — Portail usine',
  login: 'Connexion', username: "Nom d'utilisateur", secret: 'Code PIN / mot de passe', signIn: 'Se connecter', signOut: 'Déconnexion',
  tabGate: 'Portail', tabCameras: 'Caméras', tabPhones: 'Téléphones carrière', tabFleet: 'Camions et chauffeurs',
  tabUsers: 'Utilisateurs', tabIntegrity: 'Intégrité',
  chooseGate: 'Choisissez le portail', scanHere: 'Scannez le bon ici', scanning: 'Vérification…',
  outcomeACCEPT: 'Bon valide', outcomePENDING: 'Bon valide — à vérifier', outcomeREJECT: 'Bon refusé',
  ticket: 'Bon', truck: 'Camion', plate: 'Plaque', driver: 'Chauffeur', usualDriver: 'Chauffeur habituel',
  otherDriver: 'Autre chauffeur', operator: 'Opérateur carrière', issued: 'Émis', minutesAgo: 'il y a {n} min',
  pictures: 'Photos des caméras', noPictures: 'Aucune caméra n\'a pris de photo', decide: 'Décision du gardien',
  dFULL: 'Plein', dPARTIAL: 'Partiel', dEMPTY: 'Vide', dDIFFERENT_TRUCK: 'Autre camion', dPLATE_UNREADABLE: 'Plaque illisible',
  stateENTERED: 'Entrée acceptée', statePENDING_REVIEW: 'Envoyé en vérification', nextTruck: 'Camion suivant (Échap)',
  cameras: 'Caméras', discover: 'Détecter les caméras du réseau', discovering: 'Recherche…', found: 'Appareils trouvés',
  noneFound: 'Aucun appareil trouvé. Vérifiez que le serveur et les caméras sont sur le même réseau, ou ajoutez la caméra à la main.',
  use: 'Utiliser', onvifAddress: 'Adresse ONVIF', camUser: 'Utilisateur caméra', camPassword: 'Mot de passe caméra',
  connect: 'Connecter', channels: 'Canaux', preview: 'Aperçu', add: 'Ajouter', save: 'Enregistrer', label: 'Nom',
  role: 'Rôle', rolePLATE: 'Plaque', roleCARGO: 'Chargement', roleOVERVIEW: 'Vue générale', gate: 'Portail',
  captureOnScan: 'Photographie au scan', frames: 'Nombre de photos', intervalMs: 'Intervalle (ms)',
  manualCamera: 'Ajouter une caméra par adresse de photo', snapshotUrl: 'Adresse de la photo (snapshot)', test: 'Tester',
  lastOk: 'Dernier succès', lastError: 'Dernière erreur', active: 'Actif', version: 'Version', noSnapshot: 'Ce canal ne fournit pas de photo',
  gates: 'Portails', newGate: 'Nouveau portail', code: 'Code', name: 'Nom', create: 'Créer',
  phones: 'Téléphones carrière', newCode: "Nouveau code d'enregistrement", codeFor: 'Nom du téléphone', expires: 'Expire',
  scanWithPhone: "Scannez ce code depuis l'application carrière (Wi-Fi de l'usine). Utilisable une seule fois.",
  revoke: 'Révoquer', revokeReason: 'Motif', revoked: 'Révoqué', enrolled: 'Enregistré',
  sites: 'Sites', quarry: 'Carrière', factory: 'Usine', geofence: 'Limites de la carrière (une ligne par point : latitude, longitude)',
  saveGeofence: 'Enregistrer les limites', hasGeofence: 'Limites définies',
  drivers: 'Chauffeurs', trucks: 'Camions', fullName: 'Nom complet', phone: 'Téléphone', employee: 'Salarié',
  contractor: 'Sous-traitant', truckId: 'N° camion (fixe)', fleetNumber: 'N° de parc', model: 'Modèle',
  defaultDriver: 'Chauffeur habituel', changePlate: 'Changer la plaque', enable: 'Activer', disable: 'Désactiver',
  users: 'Utilisateurs', language: 'Langue', setSecret: 'Définir le code', locked: 'Bloqué', lastLogin: 'Dernière connexion',
  rQUARRY_OPERATOR: 'Opérateur carrière', rGATEKEEPER: 'Gardien', rSUPERVISOR: 'Superviseur', rACCOUNTANT: 'Comptable',
  rMANAGER: 'Directeur', rADMIN: 'Administrateur', rAUDITOR: 'Auditeur',
  integrity: 'Intégrité du registre', verify: 'Vérifier la chaîne', chainOk: 'Registre intact : {n} événements vérifiés',
  chainBad: "Falsification détectée à l'événement {id} : {problem}", anchor: 'Créer un point de référence',
  anchorNote: "Notez ou envoyez cette empreinte hors de l'usine : elle révèle toute réécriture complète du registre.",
  gaps: 'Numéros de bons manquants', noGaps: 'Aucun manque',
  error: 'Erreur', close: 'Fermer', yes: 'Oui', no: 'Non', none: '—',
}

// Gate reason codes and server error codes, shown to the gatekeeper in plain words.
const reasons: Record<Lang, Record<string, string>> = {
  ar: {
    MALFORMED: 'ليست تذكرة TruckGate أو تالفة', UNKNOWN_DEVICE: 'هاتف غير مسجّل — تذكرة مزورة', WRONG_DEVICE_KIND: 'ليست تذكرة مقلع',
    BAD_SIGNATURE: 'التوقيع غير صحيح — تذكرة مزورة أو معدّلة', WRONG_KIND: 'ليست تذكرة نقل', DEVICE_REVOKED: 'الهاتف ملغى',
    ISSUED_BEFORE_REVOCATION: 'صدرت قبل إلغاء الهاتف', ALREADY_USED: 'هذه التذكرة استُعملت من قبل', VOIDED: 'التذكرة ملغاة',
    UNKNOWN_TRUCK: 'شاحنة غير معروفة', TRUCK_GAP: 'نفس الشاحنة دخلت قبل أقل من 20 دقيقة', ISSUED_IN_FUTURE: 'تاريخ التذكرة في المستقبل',
    TOO_EARLY: 'وصلت أسرع من الممكن', EXPIRED: 'التذكرة قديمة (أكثر من 90 دقيقة)', NO_GPS: 'صدرت بلا GPS',
    OUTSIDE_GEOFENCE: 'صدرت خارج المقلع', CLOCK_MISMATCH: 'ساعة الهاتف غير مضبوطة', DRIVER_CHANGED: 'سائق غير معتاد',
    GATE_EMPTY: 'البواب: فارغة', GATE_DIFFERENT_TRUCK: 'البواب: شاحنة مختلفة', GATE_PLATE_UNREADABLE: 'البواب: لوحة غير مقروءة',
    LOGIN_FAILED: 'اسم المستخدم أو الرمز خاطئ', ACCOUNT_LOCKED: 'الحساب مقفل مؤقتاً بعد محاولات خاطئة',
    ALREADY_DECIDED: 'سُجّل القرار من قبل', SCAN_REJECTED: 'التذكرة مرفوضة، لا قرار', ONVIF_FAILED: 'فشل الاتصال بالكاميرا',
    SNAPSHOT_FAILED: 'لم تعطِ الكاميرا صورة', SNAPSHOT_TAMPERED: 'الصورة المحفوظة عُدّلت!', CSRF: 'طلب غير مسموح',
  },
  fr: {
    MALFORMED: 'Pas un bon TruckGate ou endommagé', UNKNOWN_DEVICE: 'Téléphone inconnu — bon falsifié', WRONG_DEVICE_KIND: "Pas un bon de carrière",
    BAD_SIGNATURE: 'Signature invalide — bon falsifié ou modifié', WRONG_KIND: 'Pas un bon de transport', DEVICE_REVOKED: 'Téléphone révoqué',
    ISSUED_BEFORE_REVOCATION: 'Émis avant la révocation', ALREADY_USED: 'Ce bon a déjà été utilisé', VOIDED: 'Bon annulé',
    UNKNOWN_TRUCK: 'Camion inconnu', TRUCK_GAP: 'Même camion entré il y a moins de 20 min', ISSUED_IN_FUTURE: 'Date du bon dans le futur',
    TOO_EARLY: 'Arrivé plus vite que possible', EXPIRED: 'Bon trop ancien (plus de 90 min)', NO_GPS: 'Émis sans GPS',
    OUTSIDE_GEOFENCE: 'Émis hors de la carrière', CLOCK_MISMATCH: 'Horloge du téléphone déréglée', DRIVER_CHANGED: 'Autre chauffeur',
    GATE_EMPTY: 'Gardien : vide', GATE_DIFFERENT_TRUCK: 'Gardien : autre camion', GATE_PLATE_UNREADABLE: 'Gardien : plaque illisible',
    LOGIN_FAILED: 'Identifiant ou code incorrect', ACCOUNT_LOCKED: 'Compte bloqué temporairement après des erreurs',
    ALREADY_DECIDED: 'Décision déjà enregistrée', SCAN_REJECTED: 'Bon refusé, pas de décision', ONVIF_FAILED: 'Connexion caméra impossible',
    SNAPSHOT_FAILED: "La caméra n'a pas donné de photo", SNAPSHOT_TAMPERED: 'La photo stockée a été modifiée !', CSRF: 'Requête refusée',
  },
}

export const dictionaries: Record<Lang, Record<Key, string>> = { ar, fr }

export interface I18n {
  lang: Lang
  t: (key: Key, vars?: Record<string, string | number>) => string
  reason: (code: string) => string
}

export function makeI18n(lang: Lang): I18n {
  const d = dictionaries[lang]
  return {
    lang,
    t: (key, vars) => Object.entries(vars ?? {}).reduce((s, [k, v]) => s.replace(`{${k}}`, String(v)), d[key]),
    reason: (code) => reasons[lang][code] ?? code,
  }
}

export const I18nContext = createContext<I18n>(makeI18n('ar'))
export const useI18n = () => useContext(I18nContext)
