import { api, canWrite } from '../api.js';
import { html, badge, fmtDate, select, qs, pager, openModal, toast, go, rerender } from '../ui.js';
import { icon } from '../icons.js';

const PAGE = 25;
const STATUS_KIND = { Pending: 'warn', Processing: 'info', Processed: 'ok', Failed: 'bad' };

export default async function events(segs, query) {
  const skip = Number(query.skip) || 0;
  const status = query.status || '';
  const appId = query.appId || '';
  const q = query.q || '';
  const write = canWrite();

  const [data, apps] = await Promise.all([
    api.get(`/webhook-events${qs({ status, appId, search: q, skip, take: PAGE })}`),
    api.get('/apps'),
  ]);
  const here = (extra) => '#/events' + qs({ status, appId, q, ...extra });
  const byId = new Map(data.items.map((e) => [e.id, e]));

  return {
    title: 'LINE events',
    actions: {
      filter: (el) => go(here({ [el.dataset.key]: el.value, skip: 0 })),
      search: (el) => go(here({ q: el.value.trim(), skip: 0 })),
      page: (el) => go(here({ skip: el.dataset.skip })),
      refresh: () => rerender(),
      openEvent: async (el) => {
        const detail = await api.get('/webhook-events/' + el.dataset.id);
        const e = detail.event;
        let payload = detail.payload;
        try { payload = JSON.stringify(JSON.parse(detail.payload), null, 2); } catch { /* show as stored */ }
        openModal({
          title: `${e.eventType} event`, wide: true, noSubmit: true,
          body: html`
            <dl class="kv">
              <dt>App</dt><dd><span class="mono">${e.appCode}</span></dd>
              <dt>Channel</dt><dd><code>${e.channelId}</code></dd>
              <dt>Status</dt><dd>${badge(e.status, STATUS_KIND[e.status])} after ${e.attempts} attempt${e.attempts === 1 ? '' : 's'}${e.isRedelivery ? ' (redelivery)' : ''}</dd>
              <dt>From (LINE user)</dt><dd>${e.sourceUserId ? html`<code>${e.sourceUserId}</code>` : '—'}</dd>
              <dt>Received</dt><dd>${fmtDate(e.receivedAt)}</dd>
              <dt>Processed</dt><dd>${fmtDate(e.processedAt)}</dd>
            </dl>
            ${e.lastError ? html`<div class="notice bad">${e.lastError}</div>` : ''}
            <h3>Payload</h3>
            <pre class="payload">${payload}</pre>
            ${write && (e.status === 'Failed' || e.status === 'Pending')
              ? html`<div class="row end mb"><button type="button" class="btn primary" data-action="retryEvent" data-id="${e.id}">Retry now</button></div>` : ''}`,
        });
      },
      retryEvent: async (el) => {
        await api.post(`/webhook-events/${el.dataset.id}/retry`);
        document.getElementById('modal').close();
        toast('Queued for another try');
        rerender();
      },
    },
    onMount: (root) => {
      const input = root.querySelector('[data-input="search"]');
      if (input && q) { input.focus(); input.setSelectionRange(q.length, q.length); }
    },
    view: html`
      <div class="page-head">
        <div><h1>LINE events</h1><div class="sub">Webhook events received from LINE, waiting or already handled</div></div>
        <button class="btn" data-action="refresh">${icon('refresh')}Refresh</button>
      </div>
      <div class="filters">
        <select data-change="filter" data-key="status" aria-label="Status">
          <option value="">All statuses</option>
          ${['Pending', 'Processing', 'Processed', 'Failed'].map((s) => html`<option value="${s}" ${s === status ? 'selected' : ''}>${s}</option>`)}
        </select>
        <select data-change="filter" data-key="appId" aria-label="App">
          <option value="">All apps</option>
          ${apps.map((a) => html`<option value="${a.id}" ${a.id === appId ? 'selected' : ''}>${a.code}</option>`)}
        </select>
        <input type="search" placeholder="Search type, channel, LINE user, event id" value="${q}" data-input="search" aria-label="Search events">
      </div>
      ${data.items.length === 0
        ? html`<div class="card empty">${status || appId || q ? 'No event matches these filters.' : 'No LINE event has arrived yet.'}</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>Received</th><th>App</th><th>Type</th><th>Status</th><th>Tries</th><th>Error</th><th></th></tr></thead>
            <tbody>${data.items.map((e) => html`<tr>
              <td>${fmtDate(e.receivedAt)}</td>
              <td><span class="mono">${e.appCode}</span><br><span class="muted">${e.channelId}</span></td>
              <td>${e.eventType}</td>
              <td>${badge(e.status, STATUS_KIND[e.status])}</td>
              <td>${e.attempts}</td>
              <td class="muted">${e.lastError ? e.lastError.slice(0, 80) : ''}</td>
              <td class="actions"><button class="btn small" data-action="openEvent" data-id="${e.id}">Open</button></td>
            </tr>`)}</tbody>
          </table></div>
          ${pager({ skip, take: PAGE, total: data.total })}`}`,
  };
}
