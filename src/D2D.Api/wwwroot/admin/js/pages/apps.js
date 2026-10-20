import { api, canWrite } from '../api.js';
import { html, badge, yesNo, field, area, check, lines, openModal, showSecret, go } from '../ui.js';
import appDetail from './appDetail.js';

export default async function apps(segs, query) {
  if (segs[1]) return appDetail(segs[1], segs[2] || 'settings', query);

  const list = await api.get('/apps');
  const write = canWrite();

  return {
    title: 'Apps',
    actions: {
      newApp: () => openModal({
        title: 'Register an app',
        submitLabel: 'Register',
        body: html`
          ${field('Code', 'code', { required: true, pattern: '[a-z0-9][a-z0-9\\-]{1,62}', placeholder: 'booksabuy', hint: 'Lowercase letters, digits and dashes. This is the X-App-Id the app sends; it cannot be changed later.' })}
          ${field('Name', 'name', { required: true, placeholder: 'Booksabuy' })}
          ${check('Allow self-registration', 'allowSelfRegistration', true, 'Anyone can create an account. Turn off to add members only by hand.')}
          ${check('Sign-in only through the app’s backend', 'requireAppKeyForAuth', false, 'Login, register and refresh then also need the app’s API key.')}
          ${area('Redirect URIs', 'redirectUris', { hint: 'One per line. Where LINE Login may send the browser back to.' })}`,
        onSubmit: async (fd, close) => {
          const created = await api.post('/apps', {
            code: fd.get('code'),
            name: fd.get('name'),
            allowSelfRegistration: fd.get('allowSelfRegistration') === 'on',
            requireAppKeyForAuth: fd.get('requireAppKeyForAuth') === 'on',
            redirectUris: lines(fd.get('redirectUris')),
          });
          close();
          showSecret('API key of ' + created.app.code, 'Copy this key now. It is shown only once; D2D keeps just a hash. The app’s backend sends it as X-Api-Key.', created.apiKey);
          go('#/apps/' + created.app.id);
        },
      }),
    },
    view: html`
      <div class="page-head">
        <div><h1>Apps</h1><div class="sub">Every application that uses the central API</div></div>
        ${write ? html`<button class="btn primary" data-action="newApp">Register app</button>` : ''}
      </div>
      ${list.length === 0
        ? html`<div class="card empty">No app registered yet.</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>App</th><th>Status</th><th>Self-registration</th><th>Backend-only sign-in</th><th>Database</th><th>LINE channels</th></tr></thead>
            <tbody>${list.map((a) => html`<tr>
              <td><a href="#/apps/${a.id}"><strong>${a.name}</strong></a><br><span class="muted mono">${a.code}</span></td>
              <td>${badge(a.isActive ? 'Active' : 'Disabled', a.isActive ? 'ok' : 'bad')}</td>
              <td>${yesNo(a.allowSelfRegistration)}</td>
              <td>${yesNo(a.requireAppKeyForAuth)}</td>
              <td>${a.databaseProvider ? badge(a.databaseProvider, 'info') : badge('none')}</td>
              <td>${a.lineChannels.length}</td>
            </tr>`)}</tbody>
          </table></div>`}`,
  };
}
