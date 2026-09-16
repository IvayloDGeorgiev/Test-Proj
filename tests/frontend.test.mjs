import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../Test Proj/wwwroot/app.js', import.meta.url), 'utf8');
class Element {
  textContent = ''; children = []; disabled = false; hidden = false; value = ''; className = ''; events = {};
  append(...values) { this.children.push(...values); }
  replaceChildren(...values) { this.children = values; }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, callback) { this.events[name] = callback; }
  reportValidity() { return true; }
  set innerHTML(_) { throw Error('HTML interpretation is forbidden for untrusted data'); }
}
const flush = () => new Promise(resolve => setImmediate(resolve));
let instance = 0;
async function harness(responder) {
  const ids = ['dataset','latitude','longitude','month','search','filters','records','table-head','table-body','status','result','count','page','previous','next','sync','load','location'];
  const nodes = Object.fromEntries(ids.map(id => [id, new Element()]));
  nodes.dataset.value = 'forces'; nodes.latitude.value = '53.8'; nodes.longitude.value = '-1.5'; nodes.month.value = '2024-01';
  globalThis.document = { getElementById: id => nodes[id], createElement: () => new Element(), querySelectorAll: () => ['dataset','latitude','longitude','month','previous','next','sync','load'].map(id => nodes[id]) };
  const requests = [];
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return responder(url, options, requests.length); };
  const app = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${instance++}`);
  await flush();
  return { nodes, requests, app };
}
const response = data => ({ ok: true, json: async () => data });
const empty = () => response({ items: [], hasMore: false });

test('empty saved data displays empty state and enables sync', async () => {
  const { nodes, requests } = await harness(empty);
  assert.match(nodes.status.textContent, /No saved records/);
  assert.equal(nodes.sync.disabled, false);
  assert.match(requests[0].url, /^\/api\/data\/forces\?/);
});

test('sync disables controls, refuses repeated click and refreshes after commit', async () => {
  let complete;
  const { nodes, requests, app } = await harness((url, options) => options.method === 'POST' ? new Promise(resolve => { complete = resolve; }) : empty());
  const pending = app.run(true);
  await flush();
  assert.equal(nodes.sync.disabled, true); assert.match(nodes.status.textContent, /Synchronizing/);
  await app.run(true); assert.equal(requests.length, 2);
  complete(response({ inserted: 1, updated: 2, unchanged: 3, removed: 0 }));
  await pending;
  assert.equal(requests.length, 3); assert.match(requests[2].url, /^\/api\/data\//);
  assert.equal(requests[1].options.headers['X-Police-Sync'], '1');
  assert.equal(nodes.sync.disabled, false); assert.match(nodes.result.textContent, /1 added, 2 updated/);
});

test('database failure shows safe message, no payload and allows retry', async () => {
  const { nodes } = await harness(() => ({ ok: false, json: async () => ({ code: 'database_unavailable', detail: 'synthetic-private-marker' }) }));
  assert.match(nodes.status.textContent, /Database unavailable/);
  assert.equal(nodes.status.className, 'error'); assert.equal(nodes.sync.disabled, false);
  assert.doesNotMatch(nodes.status.textContent, /synthetic-private-marker/);
});

test('untrusted stored content is rendered as literal text', async () => {
  const payload = '<img src=x onerror=alert(1)>';
  const { nodes } = await harness(() => response({ items: [{ key: 'a', data: { name: payload } }], hasMore: true }));
  assert.equal(nodes['table-body'].children[0].children[1].textContent, payload);
  assert.equal(nodes.next.disabled, false); assert.equal(nodes.previous.disabled, true);
});

test('failed sync does not refresh and releases busy state', async () => {
  const { nodes, requests, app } = await harness((url, options) => options.method === 'POST' ? { ok: false, json: async () => ({ code: 'operation_busy' }) } : empty());
  await app.run(true);
  assert.equal(requests.length, 2); assert.match(nodes.status.textContent, /Another operation/); assert.equal(nodes.sync.disabled, false);
});

test('successful sync with failed refresh retains committed result', async () => {
  const { nodes, app } = await harness((url, options, count) => count === 1 ? empty() : options.method === 'POST' ? response({ inserted: 1, updated: 0, unchanged: 0, removed: 0 }) : Promise.reject(Error('Network unavailable')));
  await app.run(true);
  assert.match(nodes.result.textContent, /Sync complete/); assert.equal(nodes.status.className, 'error'); assert.equal(nodes.sync.disabled, false);
});
