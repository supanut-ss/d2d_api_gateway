import { api, canWrite } from '../api.js';
import { html, badge, fmtDate, field, qs, pager, openModal, confirmAction, toast, go, rerender } from '../ui.js';

const PAGE = 25;

export default async function users(segs, query) {
  return segs[1] ? detail(segs[1]) : list(query);
}

async function list(query) {
  const skip = Number(query.skip) || 0;
  const q = query.q || '';
  const data = await api.get(`/users${qs({ search: q, skip, take: PAGE })}`);

  return {
    title: 'Users',
    actions: {
      search: (el) => go('#/users' + qs({ q: el.value.trim() })),
      page: (el) => go('#/users' + qs({ q, skip: el.dataset.skip })),
    },
    onMount: (root) => {
      const input = root.querySelector('[data-input="search"]');
      if (input && q) { input.focus(); input.setSelectionRange(q.length, q.length); }
    },
    view: html`
      <div class="page-head"><div><h1>Users</h1><div class="sub">Everyone who has an account on any app</div></div></div>
      <div class="filters">
        <input type="search" placeholder="Search name or email" value="${q}" data-input="search" aria-label="Search users">
      </div>
      ${data.items.length === 0
        ? html`<div class="card empty">${q ? 'Nobody matches.' : 'No users yet.'}</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>User</th><th>Apps</th><th>Sign-in</th><th>Status</th><th>Last sign-in</th></tr></thead>
            <tbody>${data.items.map((u) => html`<tr>
              <td><a href="#/users/${u.id}"><strong>${u.displayName}</strong></a><br><span class="muted">${u.email || '—'}</span></td>
              <td>${u.apps.length ? u.apps.map((a) => badge(a.code, 'info')) : html`<span class="muted">none</span>`}</td>
              <td>${u.loginMethods.map((x) => badge(x))}</td>
              <td>${badge(u.isActive ? 'Active' : 'Disabled', u.isActive ? 'ok' : 'bad')}</td>
              <td>${fmtDate(u.lastLoginAt)}</td>
            </tr>`)}</tbody>
          </table></div>
          ${pager({ skip, take: PAGE, total: data.total })}`}`,
  };
}

async function detail(id) {
  const { user, identities, activeSessions } = await api.get('/users/' + id);
  const write = canWrite();

  return {
    title: user.displayName,
    actions: {
      toggleActive: async () => {
        const disable = user.isActive;
        const ok = await confirmAction({
          title: disable ? 'Disable this user' : 'Enable this user', danger: disable, confirmLabel: disable ? 'Disable' : 'Enable',
          message: disable
            ? `${user.displayName} can no longer sign in to any app and every session ends.`
            : `${user.displayName} can sign in again.`,
        });
        if (!ok) return;
        await api.put(`/users/${id}/active`, { isActive: !disable });
        toast(disable ? 'User disabled' : 'User enabled');
        rerender();
      },
      revokeSessions: async () => {
        const ok = await confirmAction({
          title: 'End all sessions', confirmLabel: 'End sessions',
          message: `${user.displayName} is signed out of every app (access tokens already issued last up to 15 more minutes).`,
        });
        if (!ok) return;
        await api.post(`/users/${id}/revoke-sessions`);
        toast('Sessions ended');
        rerender();
      },
      resetPassword: () => openModal({
        title: 'Set a new password',
        danger: true,
        submitLabel: 'Set password',
        body: html`<p class="muted">Sets the password of ${user.displayName} and signs them out everywhere. Tell them the new password through a safe channel.</p>
          ${field('New password', 'password', { type: 'password', required: true, minlength: 8, autocomplete: 'new-password' })}`,
        onSubmit: async (fd, close) => {
          await api.post(`/users/${id}/password`, { newPassword: fd.get('password') });
          close();
          toast('Password changed');
          rerender();
        },
      }),
    },
    view: html`
      <div class="page-head">
        <div>
          <h1>${user.displayName} ${badge(user.isActive ? 'Active' : 'Disabled', user.isActive ? 'ok' : 'bad')}</h1>
          <div class="sub"><a href="#/users">Users</a> / ${user.email || user.id}</div>
        </div>
        ${write ? html`<div class="row">
          <button class="btn" data-action="revokeSessions">End sessions</button>
          ${user.loginMethods.includes('password') ? html`<button class="btn" data-action="resetPassword">Set password</button>` : ''}
          <button class="btn ${user.isActive ? 'danger' : 'primary'}" data-action="toggleActive">${user.isActive ? 'Disable' : 'Enable'}</button>
        </div>` : ''}
      </div>

      <div class="card">
        <h2>Account</h2>
        <dl class="kv">
          <dt>User id</dt><dd><code>${user.id}</code></dd>
          <dt>Email</dt><dd>${user.email || '—'}</dd>
          <dt>Created</dt><dd>${fmtDate(user.createdAt)}</dd>
          <dt>Last sign-in</dt><dd>${fmtDate(user.lastLoginAt)}</dd>
          <dt>Open sessions</dt><dd>${activeSessions}</dd>
          <dt>Sign-in methods</dt><dd>${user.loginMethods.length ? user.loginMethods.map((x) => badge(x)) : '—'}</dd>
        </dl>
      </div>

      <div class="card">
        <h2>Apps and roles</h2>
        ${user.apps.length === 0
          ? html`<div class="empty">Not a member of any app.</div>`
          : html`<div class="table-wrap"><table>
              <thead><tr><th>App</th><th>Roles</th></tr></thead>
              <tbody>${user.apps.map((a) => html`<tr>
                <td><a href="#/apps/${a.appId}/members"><span class="mono">${a.code}</span></a></td>
                <td>${a.roles.map((r) => badge(r, 'info'))}</td>
              </tr>`)}</tbody>
            </table></div>`}
      </div>

      <div class="card">
        <h2>Linked accounts</h2>
        ${identities.length === 0
          ? html`<div class="empty">No external account linked.</div>`
          : html`<div class="table-wrap"><table>
              <thead><tr><th>Provider</th><th>Scope (LINE provider)</th><th>Channel</th><th>Linked</th></tr></thead>
              <tbody>${identities.map((i) => html`<tr>
                <td>${badge(i.provider)}</td><td><code>${i.providerScope}</code></td><td><code>${i.channelId}</code></td><td>${fmtDate(i.linkedAt)}</td>
              </tr>`)}</tbody>
            </table></div>`}
      </div>`,
  };
}
