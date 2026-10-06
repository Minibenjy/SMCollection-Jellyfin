'use strict';
// Exercises the page logic of Web/artworkRefresher.js (rotation per page load) against a tiny simulated DOM.
// Run: node tests/js/artworkRefresher.rotation.test.js
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const SCRIPT = fs.readFileSync(path.join(__dirname, '../../plugins/Jellyfin.Plugin.ArtworkRefresher/Web/artworkRefresher.js'), 'utf8');
let passed = 0;
function check(cond, name) { assert.ok(cond, name); passed++; }
function eq(a, b, name) { assert.strictEqual(a, b, name + ' (expected ' + b + ', got ' + a + ')'); passed++; }

function makeEnv(caps, availableByCall) {
    const all = [];
    const calls = [];
    const images = [];
    function el(tag, attrs, bg) {
        const attributes = Object.assign({}, attrs || {});
        const e = {
            nodeType: 1, tagName: tag, style: { backgroundImage: bg || '' },
            getAttribute(n) { return n in attributes ? attributes[n] : null; },
            setAttribute(n, v) { attributes[n] = String(v); },
            removeAttribute(n) { delete attributes[n]; },
            querySelectorAll() { return []; }, addEventListener() {}, classList: { contains() { return false; } }
        };
        all.push(e);
        return e;
    }
    const body = el('BODY');
    body.querySelectorAll = () => all.filter(e => e !== body && ((e.tagName === 'IMG' && (e.getAttribute('src') || '').includes('/Images/')) || (e.style.backgroundImage || '').includes('/Images/')));
    class FakeImage { constructor() { images.push(this); } set src(v) { this._src = v; } get src() { return this._src; } }
    const window = { ApiClient: {
        getUrl: p => 'http://srv/' + p,
        ajax(o) {
            calls.push({ type: o.type, url: o.url, data: o.data ? JSON.parse(o.data) : null });
            if (o.url.endsWith('ArtworkRefresher/Capabilities')) { return Promise.resolve(caps); }
            if (o.url.endsWith('Rotating/Available')) { const keys = {}; availableByCall(JSON.parse(o.data).Ids).forEach(k => { keys[k] = '123.sig' + k.slice(0, 3); }); return Promise.resolve({ keys }); }
            return Promise.reject(new Error('unexpected ' + o.url));
        } } };
    window.window = window;
    const sandbox = { window, document: { documentElement: { lang: 'en' }, body, addEventListener() {} }, navigator: { language: 'en' },
        MutationObserver: class { observe() {} }, Image: FakeImage, setTimeout, clearTimeout, Date, Math, JSON, Promise, console };
    vm.runInNewContext(SCRIPT, sandbox);
    return { window, body, el, calls, images, rot: window.__artworkRefresherRotation, sandbox };
}

const A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', B = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', C = 'cccccccccccccccccccccccccccccccc';

(async () => {
    // --- parseTarget
    const env0 = makeEnv({ RotationPerLoadTypes: [] }, () => []);
    const p = env0.rot.parseTarget;
    eq(p('/Items/' + A + '/Images/Primary?fillHeight=446&tag=x').id, A, 'primary with query');
    eq(p('http://h/Items/' + A + '/Images/Backdrop/0?maxWidth=1').type, 'Backdrop', 'backdrop index 0');
    eq(p('/Items/' + A + '/Images/Backdrop/1?x=1'), null, 'backdrop index 1 is not rotated');
    eq(p('/Items/AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA/Images/Logo').id, 'a'.repeat(32), 'dashed guid is normalised');
    eq(p('/Items/' + A + '/Images/Banner'), null, 'unsupported type');
    eq(p('/Users/' + A + '/Images/Primary'), null, 'not an item image');

    // --- nothing happens when no type is per-load
    const env1 = makeEnv({ RotationPerLoadTypes: [] }, () => { throw new Error('must not ask'); });
    const e1 = env1.el('DIV', {}, 'url("/Items/' + A + '/Images/Primary?x=1")');
    await env1.rot.scanNow(env1.body);
    eq(e1.style.backgroundImage, 'url("/Items/' + A + '/Images/Primary?x=1")', 'no per-load type: untouched');
    eq(env1.calls.filter(c => c.url.includes('Available')).length, 0, 'no per-load type: no Available call');

    // --- cards and detail images
    const env = makeEnv({ RotationPerLoadTypes: ['Primary', 'Backdrop'] }, ids => ids.filter(i => i !== C).map(i => i + ':Primary').concat([A + ':Backdrop']));
    const card = env.el('DIV', { class: 'cardImageContainer' }, 'url("/Items/' + A + '/Images/Primary?fillHeight=446&tag=t1")');
    const missing = env.el('DIV', {}, 'url("/Items/' + C + '/Images/Primary?tag=t")');
    const logo = env.el('DIV', {}, 'url("/Items/' + A + '/Images/Logo?tag=t")');
    const hero = env.el('IMG', { src: '/Items/' + A + '/Images/Backdrop/0?maxWidth=1920', srcset: 'a 1x, b 2x' });
    const other = env.el('IMG', { src: '/Items/' + B + '/Images/Primary?tag=t' });
    const done = await env.rot.scanNow(env.body);
    eq(done, 3, 'three images are swapped (card, hero, other)');
    eq(env.images.length, 3, 'three probes were created');
    env.images.forEach(i => i.onload());
    check(card.style.backgroundImage.includes('http://srv/ArtworkRefresher/Rotating/' + A + '/Primary'), 'card points at the rotating endpoint');
    check(/[?]s=123[.]sig[a-z]{3}&n=[a-z0-9]+/.test(card.style.backgroundImage), 'address carries the signed token and a page-view nonce');
    const probeUrl = env.images[0].src;
    check(card.style.backgroundImage.includes(probeUrl) || hero.getAttribute('src') === probeUrl || other.getAttribute('src') === probeUrl, 'the displayed address is exactly the preloaded one (same draw)');
    eq(new Set(env.images.map(i => i.src.split('&n=')[1])).size, 3, 'each swap gets its own nonce');
    eq(card.getAttribute('data-arf-orig').includes('tag=t1'), true, 'original address is remembered');
    check(hero.getAttribute('src').includes('/Rotating/' + A + '/Backdrop'), 'hero <img> points at the rotating endpoint');
    eq(hero.getAttribute('srcset'), null, 'srcset removed so the browser does not pick the old image');
    check(other.getAttribute('src').includes('/Rotating/' + B + '/Primary'), 'second item swapped');
    check(missing.style.backgroundImage.includes('/Items/' + C + '/'), 'item the server cannot rotate keeps its image');
    check(logo.style.backgroundImage.includes('/Images/Logo'), 'a type not set per-load keeps its image');

    // --- second scan: nothing re-asked, nothing swapped twice
    const askedBefore = env.calls.filter(c => c.url.includes('Available')).length;
    const again = await env.rot.scanNow(env.body);
    eq(again, 0, 'already swapped images are left alone');
    eq(env.calls.filter(c => c.url.includes('Available')).length, askedBefore, 'known items are not asked about again within the TTL');

    // --- a probe error (404) keeps the original image
    const env2 = makeEnv({ RotationPerLoadTypes: ['Primary'] }, ids => ids.map(i => i + ':Primary'));
    const failing = env2.el('DIV', {}, 'url("/Items/' + A + '/Images/Primary?tag=z")');
    await env2.rot.scanNow(env2.body);
    env2.images.forEach(i => i.onerror());
    check(failing.style.backgroundImage.includes('tag=z'), '404 from the endpoint: original stays');

    // --- many items are asked in chunks of at most 100
    const env3 = makeEnv({ RotationPerLoadTypes: ['Primary'] }, () => []);
    for (let i = 0; i < 230; i++) {
        env3.el('DIV', {}, 'url("/Items/' + i.toString(16).padStart(32, '0') + '/Images/Primary?x=1")');
    }
    await env3.rot.scanNow(env3.body);
    const sizes = env3.calls.filter(c => c.url.includes('Available')).map(c => c.data.Ids.length);
    eq(sizes.join(','), '100,100,30', 'ids are sent in chunks of 100');

    // --- the page keeps working when the server is unreachable
    const env4 = makeEnv(null, () => []);
    env4.window.ApiClient.ajax = () => Promise.reject(new Error('down'));
    const e4 = env4.el('DIV', {}, 'url("/Items/' + A + '/Images/Primary?x=1")');
    eq(await env4.rot.scanNow(env4.body), 0, 'server down: nothing swapped, no exception');
    check(e4.style.backgroundImage.includes('/Items/'), 'server down: image untouched');

    console.log(passed + ' JS checks passed.');
})().catch(e => { console.error('FAIL:', e.message); process.exit(1); });
