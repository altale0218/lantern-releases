'use strict';
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const root = path.resolve(__dirname, '../../ui');
const assets = { '/': ['index.html', 'text/html'], ...Object.fromEntries(
    fs.readdirSync(root).filter(name => /\.(css|js|svg)$/.test(name)).map(name => ['/' + name,
        [name, name.endsWith('.css') ? 'text/css' : name.endsWith('.js') ? 'text/javascript' : 'image/svg+xml']])) };
const server = http.createServer((request, response) => {
    const asset = assets[request.url];
    if (!asset) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'Content-Type': asset[1] }).end(fs.readFileSync(path.join(root, asset[0])));
});

(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH || undefined });
    try {
        for (const platform of ['Windows', 'Android', 'iOS', 'iPadOS']) {
            for (const reducedMotion of ['no-preference', 'reduce']) {
                const page = await browser.newPage({ reducedMotion, viewport: platform === 'Windows'
                    ? { width: 960, height: 720 } : { width: 393, height: 720 } });
                // This test models an existing user. The first-run dialog takes focus by design.
                await page.addInitScript(() => {
                    localStorage.setItem('lantern.welcome.v1', 'done');
                    localStorage.setItem('lantern.guide.v1', 'done');
                });
                await page.goto('http://127.0.0.1:' + server.address().port);
                await page.evaluate(platform => {
                    window.fixture = { mode: 'native', self: { id: 'local', name: '虚构设备', platform }, settings: { theme: 'light', limitMiB: 0 }, tasks: [], devices: [], pending: [] };
                    window.lantern.setState(window.fixture);
                    window.lantern.openTab('settings');
                }, platform);
                const summary = page.locator('.advanced-settings summary');
                await summary.focus();
                assert.equal(await page.getByLabel('发送速度上限').isVisible(), false);
                await summary.press('Enter');
                assert.equal(await page.getByLabel('发送速度上限').isVisible(), true);
                const animation = await page.locator('.advanced-settings .setting').evaluate(e => getComputedStyle(e).animationTimingFunction);
                if (reducedMotion === 'no-preference') assert.match(animation, /cubic-bezier/);
                else assert.equal(await page.locator('.advanced-settings .setting').evaluate(e => getComputedStyle(e).animationName), 'none');
                await page.evaluate(() => {
                    window.disclosure = document.querySelector('.advanced-settings');
                    window.fixture.settings = { ...window.fixture.settings, limitMiB: 30 };
                    for (let refresh = 0; refresh < 20; refresh++) window.lantern.setState(window.fixture);
                });
                assert.equal(await page.getByLabel('发送速度上限').inputValue(), '30');
                assert.equal(await summary.evaluate(e => e.parentElement === window.disclosure && e.parentElement.open && e === document.activeElement), true);
                await summary.press('Space');
                assert.equal(await page.getByLabel('发送速度上限').isVisible(), false);
                await page.close();
            }
        }
        console.log('PASS 8 disclosure scenarios across 4 shared layouts, with preserved focus and reduced motion');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => server.close());
