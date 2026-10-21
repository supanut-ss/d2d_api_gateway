import { api, canWrite, getSession } from '../api.js';
import { html, badge, fmtDate, field, select, openModal, confirmAction, toast, rerender } from '../ui.js';
import { iconButton } from '../icons.js';

const ROLES = [['Admin', 'Admin — can change everything'], ['Viewer', 'Viewer — can only look']];

export default async function admins() {
  const list = await api.get('/admins');
  const write = canWrite();
  const myId = getSession().admin.id;
  const find = (id) => list.find((a) => a.id === id);

  return {
    title: 'Admins',
    actions: {
      newAdmin: () => openModal({
        title: 'Add an admin',
        submitLabel: 'Add',
        body: html`
          ${field('Name', 'displayName', { required: true })}
          ${field('Email', 'email', { type: 'email', required: true })}
          ${field('Password', 'password', { type: 'password', required: true, minlength: 10, autocomplete: 'new-password', hint: 'At least 10 characters. Share it through a safe channel; they can change it after signing in.' })}
          ${select('Role', 'role', ROLES, 'Admin')}`,
        onSubmit: async (fd, close) => {
          await api.post('/admins', {
            email: fd.get('email'), displayName: fd.get('displayName'), password: fd.get('password'), role: fd.get('role'),
          });
          close();
          toast('Admin added');
          rerender();
        },
      }),
      editAdmin: (el) => {
        const a = find(el.dataset.id);
        const self = a.id === myId;
        openModal({
          title: 'Edit ' + a.displayName,
          body: html`
            ${field('Name', 'displayName', { value: a.displayName, required: true })}
            ${self
              ? html`<p class="muted">You cannot change your own role or disable yourself.</p>`
              : html`${select('Role', 'role', ROLES, a.role)}
                <label class="check"><input type="checkbox" name="isActive" ${a.isActive ? 'checked' : ''}><span>Active<span class="hint">A disabled admin cannot sign in and their sessions end.</span></span></label>`}`,
          onSubmit: async (fd, close) => {
            await api.put('/admins/' + a.id, {
              displayName: fd.get('displayName'),
              role: self ? null : fd.get('role'),
              isActive: self ? null : fd.get('isActive') === 'on',
            });
            close();
            toast('Saved');
            rerender();
          },
        });
      },
      resetAdminPassword: (el) => {
        const a = find(el.dataset.id);
        openModal({
          title: 'Set a new password for ' + a.displayName,
          danger: true,
          submitLabel: 'Set password',
          body: html`<p class="muted">Their sessions end and they sign in with the new password.</p>
            ${field('New password', 'password', { type: 'password', required: true, minlength: 10, autocomplete: 'new-password' })}`,
          onSubmit: async (fd, close) => {
            await api.post(`/admins/${a.id}/password`, { newPassword: fd.get('password') });
            close();
            toast('Password changed');
          },
        });
      },
    },
    view: html`
      <div class="page-head">
        <div><h1>Admins</h1><div class="sub">People who can sign in to this console</div></div>
        ${write ? html`<button class="btn primary" data-action="newAdmin">Add admin</button>` : ''}
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>Admin</th><th>Role</th><th>Status</th><th>Last sign-in</th><th></th></tr></thead>
        <tbody>${list.map((a) => html`<tr>
          <td><strong>${a.displayName}</strong> ${a.id === myId ? badge('you', 'info') : ''}<br><span class="muted">${a.email}</span></td>
          <td>${badge(a.role === 'Admin' ? 'Admin' : 'Viewer', a.role === 'Admin' ? 'warn' : '')}</td>
          <td>${badge(a.isActive ? 'Active' : 'Disabled', a.isActive ? 'ok' : 'bad')}</td>
          <td>${fmtDate(a.lastLoginAt)}</td>
          <td class="actions">${write ? html`
            ${iconButton('edit', 'Edit admin', { data: { action: 'editAdmin', id: a.id } })}
            ${iconButton('key', 'Set password', { data: { action: 'resetAdminPassword', id: a.id } })}` : ''}</td>
        </tr>`)}</tbody>
      </table></div>
      <p class="muted">The static <code>Admin:ApiKey</code> from the server configuration still works for scripts and as a way back in. Keep it secret.</p>`,
  };
}
