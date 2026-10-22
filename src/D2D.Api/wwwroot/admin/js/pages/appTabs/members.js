import { api } from '../../api.js';
import {
  html, badge, fmtDate, field, csv, qs, pager, openModal, confirmAction, toast, go, rerender,
} from '../../ui.js';
import { iconButton } from '../../icons.js';

const PAGE = 25;

export default async function members(app, write, query) {
  const id = app.id;
  const skip = Number(query.skip) || 0;
  const q = query.q || '';
  const data = await api.get(`/apps/${id}/members${qs({ search: q, skip, take: PAGE })}`);
  const here = (extra) => `#/apps/${id}/members${qs({ q, ...extra })}`;

  return {
    actions: {
      search: (el) => go(here({ q: el.value.trim(), skip: 0 })),
      page: (el) => go(here({ skip: el.dataset.skip })),
      addMember: () => openModal({
        title: 'Add a member',
        submitLabel: 'Add',
        body: html`
          ${field('Email', 'email', { type: 'email', required: true })}
          ${field('Name', 'displayName', { hint: 'Used only when a new user is created.' })}
          ${field('Password', 'password', { type: 'password', minlength: 8, autocomplete: 'new-password', hint: 'Required only when this email is not a user yet (min 8). Existing users keep their own password.' })}
          ${field('Roles', 'roles', { value: 'user', hint: 'Comma separated, e.g. user, editor. Lowercase letters, digits and _ : . -' })}`,
        onSubmit: async (fd, close) => {
          await api.post(`/apps/${id}/members`, {
            email: fd.get('email'), displayName: fd.get('displayName') || null,
            password: fd.get('password') || null, roles: csv(fd.get('roles')),
          });
          close();
          toast('Member added');
          rerender();
        },
      }),
      editRoles: (el) => openModal({
        title: 'Roles of ' + el.dataset.name,
        body: field('Roles', 'roles', { value: el.dataset.roles, required: true, hint: 'Comma separated. Takes effect in the next access token (within about 15 minutes).' }),
        onSubmit: async (fd, close) => {
          await api.put(`/apps/${id}/members/${el.dataset.id}`, { roles: csv(fd.get('roles')) });
          close();
          toast('Roles saved');
          rerender();
        },
      }),
      removeMember: async (el) => {
        const ok = await confirmAction({
          title: 'Remove member', danger: true, confirmLabel: 'Remove',
          message: `Remove ${el.dataset.name} from ${app.code}? Their sessions in this app end. Their account stays.`,
        });
        if (!ok) return;
        await api.del(`/apps/${id}/members/${el.dataset.id}`);
        toast('Member removed');
        rerender();
      },
    },
    onMount: (root) => {
      const input = root.querySelector('[data-input="search"]');
      if (input && q) { input.focus(); input.setSelectionRange(q.length, q.length); }
    },
    view: html`
      <div class="filters">
        <input type="search" placeholder="Search name or email" value="${q}" data-input="search" aria-label="Search members">
        <span class="spacer"></span>
        ${write ? html`<button class="btn primary" data-action="addMember">Add member</button>` : ''}
      </div>
      ${data.items.length === 0
        ? html`<div class="card empty">${q ? 'Nobody matches.' : 'No members yet.'}</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>Member</th><th>Roles</th><th>Sign-in</th><th>Status</th><th>Last sign-in</th><th></th></tr></thead>
            <tbody>${data.items.map((m) => html`<tr>
              <td><a href="#/users/${m.userId}"><strong>${m.displayName}</strong></a><br><span class="muted">${m.email || '—'}</span></td>
              <td>${m.roles.map((r) => badge(r, 'info'))}</td>
              <td>${m.loginMethods.map((x) => badge(x))}</td>
              <td>${badge(m.isActive ? 'Active' : 'Disabled', m.isActive ? 'ok' : 'bad')}</td>
              <td>${fmtDate(m.lastLoginAt)}</td>
              <td class="actions">${write ? html`
                ${iconButton('tag', 'Edit roles', { data: { action: 'editRoles', id: m.userId, name: m.displayName, roles: m.roles.join(', ') } })}
                ${iconButton('trash', 'Remove member', { danger: true, data: { action: 'removeMember', id: m.userId, name: m.displayName } })}` : ''}</td>
            </tr>`)}</tbody>
          </table></div>
          ${pager({ skip, take: PAGE, total: data.total })}`}`,
  };
}
