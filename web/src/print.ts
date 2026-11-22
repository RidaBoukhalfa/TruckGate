import QRCode from 'qrcode'
import type { ReceiptData } from './api'
import type { I18n, Key } from './i18n'

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

/**
 * Prints the gate receipt on the printer installed on this PC (any thermal printer with a Windows/Linux
 * driver, 58 or 80 mm). Chrome started with --kiosk-printing prints without the dialog.
 */
export async function printReceipt(r: ReceiptData, i18n: I18n) {
  const { t, lang } = i18n
  const qr = await QRCode.toDataURL(r.qrText, { errorCorrectionLevel: 'M', margin: 1, scale: 4 })
  const time = new Date(r.enteredAt).toLocaleString(lang === 'ar' ? 'ar-DZ' : 'fr-DZ', { dateStyle: 'short', timeStyle: 'short' })
  const load = r.loadStatus ? t(`d${r.loadStatus}` as Key) : '—'
  const row = (k: Key, v: string | null) => `<tr><th>${esc(t(k))}</th><td>${esc(v ?? '—')}</td></tr>`
  const html = `<!doctype html><html lang="${lang}" dir="${lang === 'ar' ? 'rtl' : 'ltr'}"><head><meta charset="utf-8">
<style>
  @page { size: 80mm auto; margin: 2mm; }
  body { font-family: Tahoma, "Segoe UI", "Noto Sans Arabic", sans-serif; width: 72mm; margin: 0 auto; text-align: center; color: #000; }
  h1 { font-size: 16px; margin: 2mm 0; }
  .trip { font-size: 26px; font-weight: 800; letter-spacing: 1px; direction: ltr; }
  .fleet { font-size: 34px; font-weight: 800; }
  table { width: 100%; font-size: 13px; border-collapse: collapse; margin: 2mm 0; }
  th { text-align: start; font-weight: normal; padding: 1px 0; }
  td { text-align: end; font-weight: 700; }
  img { width: 50mm; height: 50mm; image-rendering: pixelated; }
  .foot { font-size: 12px; }
  .copy { font-size: 18px; font-weight: 800; border: 2px solid #000; margin-top: 2mm; }
  @media print { body { width: auto; } }
</style></head><body>
<h1>${esc(t('receiptTitle'))}</h1>
<div class="trip">${esc(r.tripNumber)}</div>
<div class="fleet">${esc(r.fleetNumber ?? '')}</div>
<table>
${row('plate', r.plate)}${row('driver', r.driverName)}${row('gate', r.gateName)}${row('entered', time)}${row('load', load)}
</table>
<img src="${qr}" alt="">
<div class="foot">${esc(t('receiptFooter'))}</div>
${r.printCount > 1 ? `<div class="copy">${esc(t('copyN', { n: r.printCount }))}</div>` : ''}
</body></html>`

  const frame = document.createElement('iframe')
  frame.setAttribute('aria-hidden', 'true')
  Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '0', height: '0', border: '0' })
  document.body.appendChild(frame)
  await new Promise<void>((resolve) => {
    frame.onload = () => resolve()
    frame.srcdoc = html
  })
  const img = frame.contentDocument?.querySelector('img')
  if (img && !img.complete) await new Promise((r2) => { img.onload = r2; img.onerror = r2 })
  frame.contentWindow?.focus()
  frame.contentWindow?.print()
  setTimeout(() => frame.remove(), 60_000)
}
