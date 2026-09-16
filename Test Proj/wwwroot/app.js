const get = id => document.getElementById(id);
let busy = false, offset = 0, hasMore = false, totalCount = 0;
const limit = 20;
function status(text, error = false) { get('status').textContent = text; get('status').className = error ? 'error' : ''; }
function activity(value) {
  busy = value;
  document.querySelectorAll('input,select,button').forEach(element => { element.disabled = value; });
  get('records').setAttribute('aria-busy', String(value));
  get('previous').disabled = value || offset === 0;
  get('next').disabled = value || !hasMore;
  get('location').disabled = value || get('dataset').value === 'forces';
}
function parameters() { return { latitude: Number(get('latitude').value), longitude: Number(get('longitude').value), month: get('month').value }; }
async function request(url, options) {
  const response = await fetch(url, { credentials: 'same-origin', ...options });
  if (!response.ok) {
    let code = 'request_failed';
    try { code = (await response.json()).code || code; } catch { /* Fixed safe fallback for non-JSON responses. */ }
    const messages = { database_not_configured: 'Database configuration is missing. Ask the operator to configure it.', database_unavailable: 'Database unavailable. Check that PostgreSQL is running and migrations are applied.', operation_busy: 'Another operation is running. Try again shortly.', validation_failed: 'Check the location, month and dataset.', operation_timeout: 'The operation timed out. Please try again.', upstream_invalid_payload: 'The upstream data could not be validated.' };
    throw new Error(messages[code] || 'The request failed. Please try again.');
  }
  return response.json();
}
function render(page) {
  const head = get('table-head'), body = get('table-body'); head.replaceChildren(); body.replaceChildren();
  const columns = ['key', ...new Set(page.items.flatMap(item => Object.keys(item.data)))];
  const header = document.createElement('tr'); columns.forEach(column => { const cell = document.createElement('th'); cell.textContent = column.replaceAll('_', ' '); header.append(cell); }); head.append(header);
  page.items.forEach(item => { const row = document.createElement('tr'); columns.forEach(column => { const cell = document.createElement('td'); const value = column === 'key' ? item.key : item.data[column]; cell.textContent = value === null || value === undefined ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value); row.append(cell); }); body.append(row); });
  totalCount = page.totalCount ?? 0;
  hasMore = page.hasMore;
  get('page').textContent = `Page ${Math.floor(offset / limit) + 1}`;
  get('count').textContent = `${totalCount.toLocaleString()} saved records${get('search').value ? ' match your filter' : ''}.`;
  status(page.items.length ? `Showing ${offset + 1}–${Math.min(offset + page.items.length, totalCount)}.` : 'No saved records for this selection. Use Sync to retrieve data.');
}
async function load() {
  const dataset = get('dataset').value;
  const query = new URLSearchParams({ offset, limit, search: get('search').value, ...(dataset === 'forces' ? {} : parameters()) });
  render(await request(`/api/data/${dataset}?${query}`));
}
export async function run(sync = false) {
  if (busy || !get('filters').reportValidity()) return;
  activity(true); get('result').textContent = ''; get('records').replaceChildren();
  status(sync ? 'Synchronizing data…' : 'Loading saved data…');
  try {
    if (sync) {
      const dataset = get('dataset').value;
      const result = await request(`/api/sync/${dataset}`, { method: 'POST', headers: { 'X-Police-Sync': '1', 'Content-Type': 'application/json' }, ...(dataset === 'forces' ? {} : { body: JSON.stringify(parameters()) }) });
      get('result').textContent = `Sync complete: ${result.inserted} added, ${result.updated} updated, ${result.unchanged} unchanged, ${result.removed} removed from snapshot.`;
      offset = 0;
    }
    await load();
  } catch (error) { hasMore = false; status(error instanceof Error ? error.message : 'Request failed.', true); }
  finally { activity(false); }
}
get('filters').addEventListener('submit', event => { event.preventDefault(); offset = 0; void run(); });
get('sync').addEventListener('click', () => void run(true));
get('search').addEventListener('input', () => { if (!busy) { offset = 0; void run(); } });
get('dataset').addEventListener('change', () => { get('location').hidden = get('dataset').value === 'forces'; get('location').disabled = get('location').hidden; offset = 0; void run(); });
get('previous').addEventListener('click', () => { offset = Math.max(0, offset - limit); void run(); });
get('next').addEventListener('click', () => { offset += limit; void run(); });
void run();
