const get = id => document.getElementById(id);
let busy = false, offset = 0, hasMore = false;
const limit = 50;
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
  const container = get('records'); container.replaceChildren();
  page.items.forEach(item => {
    const article = document.createElement('article'), title = document.createElement('h2'), fields = document.createElement('dl');
    title.textContent = item.data.name || item.data.category || item.data.type || item.key;
    Object.entries(item.data).forEach(([key, value]) => {
      const term = document.createElement('dt'), definition = document.createElement('dd');
      term.textContent = key.replaceAll('_', ' ');
      definition.textContent = value === null ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value);
      fields.append(term, definition);
    });
    article.append(title, fields); container.append(article);
  });
  hasMore = page.hasMore;
  get('page').textContent = `Page ${Math.floor(offset / limit) + 1}`;
  status(page.items.length ? `${page.items.length} saved records shown.` : 'No saved records for this selection. Use Sync to retrieve data.');
}
async function load() {
  const dataset = get('dataset').value;
  const query = new URLSearchParams({ offset, limit, ...(dataset === 'forces' ? {} : parameters()) });
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
get('dataset').addEventListener('change', () => { get('location').hidden = get('dataset').value === 'forces'; get('location').disabled = get('location').hidden; offset = 0; void run(); });
get('previous').addEventListener('click', () => { offset = Math.max(0, offset - limit); void run(); });
get('next').addEventListener('click', () => { offset += limit; void run(); });
void run();
