import { api } from '../api.js';
import { html, badge, fmtDate, qs, pager, go, rerender } from '../ui.js';

const PAGE = 50;

const METHOD_KIND = { POST: 'ok', PUT: 'info', DELETE: 'bad' };

export default async function audit(segs, query) {
  const skip = Number(query.skip) || 0;
  const q = query.q || '';
  const data = await api.get(`/audit${qs({ search: q, skip, take: PAGE })}`);

  return {
    title: 'Audit log',
    actions: {
      search: (el) => go('#/audit' + qs({ q: el.value.trim() })),
      page: (el) => go('#/audit' + qs({ q, skip: el.dataset.skip })),
      refresh: () => rerender(),
    },
    onMount: (root) => {
      const input = root.querySelector('[data-input="search"]');
      if (input && q) { input.focus(); input.setSelectionRange(q.length, q.length); }
    },
    view: html`
      <div class="page-head">
        <div><h1>Audit log</h1><div class="sub">Every change made through the admin API — who, what and when. Request contents are never recorded.</div></div>
        <button class="btn" data-action="refresh">Refresh</button>
      </div>
      <div class="filters">
        <input type="search" placeholder="Search by admin, path or method" value="${q}" data-input="search" aria-label="Search audit log">
      </div>
      ${data.items.length === 0
        ? html`<div class="card empty">${q ? 'Nothing matches.' : 'No change has been recorded yet.'}</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>When</th><th>Who</th><th>Change</th><th>From</th></tr></thead>
            <tbody>${data.items.map((r) => html`<tr>
              <td>${fmtDate(r.at)}</td>
              <td>${r.actor === 'admin-key' ? badge('admin key', 'warn') : r.actor}</td>
              <td>${badge(r.method, METHOD_KIND[r.method] || '')} <code>${r.path}</code></td>
              <td class="muted">${r.ip || '—'}</td>
            </tr>`)}</tbody>
          </table></div>
          ${pager({ skip, take: PAGE, total: data.total })}`}`,
  };
}
