// Signs in to the installed TruckGate web UI with Chrome and saves screenshots of a few pages.
// Usage: node ui-screens.mjs <baseUrl> <username> <password> <outDir> <chromePath>
import puppeteer from 'puppeteer-core'
import fs from 'node:fs'

const [base, user, pass, out, chrome] = process.argv.slice(2)
fs.mkdirSync(out, { recursive: true })
const browser = await puppeteer.launch({ executablePath: chrome, headless: true, args: ['--no-first-run'] })
const errors = []
try {
const page = await browser.newPage()
await page.setViewport({ width: 1366, height: 900 })
page.on('pageerror', (e) => errors.push(String(e)))
await page.goto(base, { waitUntil: 'networkidle0' })
await page.screenshot({ path: `${out}/web-1-login.png` })
const inputs = await page.$$('form input')
await inputs[0].type(user)
await inputs[1].type(pass)
await Promise.all([page.waitForResponse((r) => r.url().includes('/api/auth/login')), page.click('form button')])
await new Promise((r) => setTimeout(r, 1500))
await page.screenshot({ path: `${out}/web-2-gate.png` })
const clickButton = async (selector, labels) => {
  const ok = await page.evaluate((sel, ls) => {
    const b = [...document.querySelectorAll(sel)].find((x) => ls.some((l) => x.textContent.includes(l)))
    if (b) b.click()
    return !!b
  }, selector, labels)
  if (!ok) throw new Error(`button not found: ${labels.join(' / ')}`)
  await new Promise((r) => setTimeout(r, 1500))
}
await clickButton('button.tab', ['هواتف المقلع', 'Téléphones carrière'])
await clickButton('button', ['عرض رمز عنوان الخادم', "Afficher le code d'adresse du serveur"])
const qr = await page.$('img[alt="server address QR"]')
if (!qr) throw new Error('server address QR not shown')
await new Promise((r) => setTimeout(r, 1500))
await page.screenshot({ path: `${out}/web-3-phones.png`, fullPage: true })
await clickButton('button.tab', ['لوحة اليوم', 'Tableau du jour'])
await page.screenshot({ path: `${out}/web-4-dashboard.png`, fullPage: true })
} finally {
  await browser.close()
}
if (errors.length) { console.error('Page errors:\n' + errors.join('\n')); process.exit(1) }
console.log('screenshots saved')
