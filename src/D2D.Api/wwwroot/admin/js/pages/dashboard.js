import { api } from '../api.js';
import { html, badge, rerender } from '../ui.js';

function stat(label, value, hint, href, bad = false) {
  const inner = html`<div class="label">${label}</div><div class="value">${value}</div><div class="hint">${hint}</div>`;
  return href
    ? html`<a class="stat ${bad ? 'bad' : ''}" href="${href}">${inner}</a>`
    : html`<div class="stat ${bad ? 'bad' : ''}">${inner}</div>`;
}

export default async function dashboard() {
  const s = await api.get('/stats');
  const w = s.webhooks;

  return {
    title: 'Dashboard',
    actions: { refresh: () => rerender() },
    view: html`
      <div class="page-head">
        <div><h1>Dashboard</h1><div class="sub">What is running on the central API right now</div></div>
        <button class="btn" data-action="refresh">Refresh</button>
      </div>

      <div class="cards">
        ${stat('Apps', `${s.activeApps} / ${s.apps}`, 'active / registered', '#/apps')}
        ${stat('Users', s.users, `${s.activeUsers} active`, '#/users')}
        ${stat('Signed-in sessions', s.activeSessions, 'refresh tokens still valid')}
        ${stat('LINE events (24 h)', s.webhookEventsLast24h, `${w.processed} processed in total`, '#/events')}
        ${stat('Failed LINE events', w.failed, w.failed ? 'need attention' : 'all good', '#/events?status=Failed', w.failed > 0)}
        ${stat('Admins', s.admins, 'can sign in to this console', '#/admins')}
      </div>

      <div class="card">
        <h2>Apps</h2>
        ${s.perApp.length === 0
          ? html`<div class="empty">No app yet. <a href="#/apps">Register the first one</a>.</div>`
          : html`<div class="table-wrap"><table>
              <thead><tr><th>App</th><th>Status</th><th>Members</th><th>LINE channels</th><th>Sessions</th></tr></thead>
              <tbody>${s.perApp.map((a) => html`<tr>
                <td><a href="#/apps/${a.appId}"><strong>${a.name}</strong></a><br><span class="muted mono">${a.code}</span></td>
                <td>${badge(a.isActive ? 'Active' : 'Disabled', a.isActive ? 'ok' : 'bad')}</td>
                <td>${a.members}</td>
                <td>${a.channels}</td>
                <td>${a.activeSessions}</td>
              </tr>`)}</tbody>
            </table></div>`}
      </div>

      <div class="card">
        <h2>LINE webhook inbox</h2>
        <div class="row">
          ${badge(`${w.pending} pending`, w.pending ? 'warn' : '')}
          ${badge(`${w.processing} processing`, w.processing ? 'info' : '')}
          ${badge(`${w.processed} processed`, 'ok')}
          ${badge(`${w.failed} failed`, w.failed ? 'bad' : '')}
          <span class="spacer"></span>
          <a href="#/events">Open the inbox</a>
        </div>
      </div>`,
  };
}
