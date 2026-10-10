// Drives /settings city search in headless Chromium and reports console/page errors + server-visible failures.
// Usage: node scripts/e2e/city-search.js [query] [--key=Enter|click|none]
// Env: BASE_URL, ADMIN_EMAIL, ADMIN_PASSWORD, CHROME_PATH (auto-detects a cached Playwright chromium)
const fs = require('fs'), path = require('path'), os = require('os');
function load() {
  for (const base of [process.env.E2E_NODE_MODULES, path.join(__dirname, 'node_modules'), '/tmp/opencode/pw/node_modules'])
    if (base && fs.existsSync(path.join(base, 'playwright'))) return require(path.join(base, 'playwright'));
  throw new Error('playwright not found. Run: npm i --prefix /tmp/opencode/pw playwright');
}
function chrome() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
  const root = path.join(os.homedir(), '.cache/ms-playwright');
  const dirs = fs.existsSync(root) ? fs.readdirSync(root).filter(d => /^chromium-\d+$/.test(d)).sort().reverse() : [];
  for (const d of dirs) { const p = path.join(root, d, 'chrome-linux64/chrome'); if (fs.existsSync(p)) return p; }
}
(async () => {
  const query = process.argv[2] || 'Atlanta';
  const key = (process.argv.find(a => a.startsWith('--key=')) || '--key=Enter').split('=')[1];
  const base = process.env.BASE_URL || 'http://localhost:5099';
  const b = await load().chromium.launch({ executablePath: chrome() });
  const p = await b.newPage();
  p.on('console', m => ['error', 'warning'].includes(m.type()) && console.log('CONSOLE', m.type(), m.text().slice(0, 500)));
  p.on('pageerror', e => console.log('PAGEERROR', e.message.slice(0, 500)));
  await p.goto(base + '/settings');
  await p.fill('input[type=email]', process.env.ADMIN_EMAIL || 'me@example.com');
  await p.fill('input[type=password]', process.env.ADMIN_PASSWORD || 'dev-password-1');
  await p.keyboard.press('Enter');
  await p.waitForURL('**/settings', { timeout: 15000 });
  await p.waitForSelector('input[role=combobox]');
  await p.waitForLoadState('networkidle');
  await p.waitForTimeout(Number(process.env.CIRCUIT_WAIT_MS || 2500)); // let the Blazor circuit connect
  const box = p.locator('input[role=combobox]').first();
  await box.click();
  await box.pressSequentially(query, { delay: 60 }); // real key events, like a user typing
  await p.waitForTimeout(1500);
  console.log('options:', await p.locator('.city-option').count());
  if (key === 'click') await p.locator('.city-option').first().click();
  else if (key !== 'none') await box.press(key);
  await p.waitForTimeout(1500);
  console.log('lat/lon:', await p.locator('input[type=number]').evaluateAll(e => e.slice(0, 2).map(x => x.value)));
  console.log('error banner visible:', await p.locator('#blazor-error-ui').isVisible());
  await p.screenshot({ path: process.env.SHOT || '/tmp/opencode/e2e/city-search.png' });
  await b.close();
})().catch(e => { console.error('FAILED', e.message); process.exit(1); });
