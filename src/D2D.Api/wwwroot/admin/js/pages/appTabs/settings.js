import { api } from '../../api.js';
import {
  html, field, area, check, lines, confirmAction, showSecret, toast, go, rerender,
} from '../../ui.js';

export default async function settings(app, write) {
  const id = app.id;
  return {
    actions: {
      saveSettings: async (fd) => {
        await api.put(`/apps/${id}`, {
          name: fd.get('name'),
          isActive: fd.get('isActive') === 'on',
          allowSelfRegistration: fd.get('allowSelfRegistration') === 'on',
          requireAppKeyForAuth: fd.get('requireAppKeyForAuth') === 'on',
          redirectUris: lines(fd.get('redirectUris')),
        });
        toast('Saved');
        rerender();
      },
      rotateKey: async () => {
        const ok = await confirmAction({
          title: 'Rotate the API key', danger: true, confirmLabel: 'Rotate',
          message: 'The current key stops working immediately. Every backend that uses it must be given the new key.',
        });
        if (!ok) return;
        const { apiKey } = await api.post(`/apps/${id}/api-key/rotate`);
        showSecret('New API key of ' + app.code, 'Copy this key now. It is shown only once.', apiKey);
      },
      deleteApp: async () => {
        const ok = await confirmAction({
          title: 'Delete this app', danger: true, confirmLabel: 'Delete app', typeToConfirm: app.code,
          message: 'This removes the app, its LINE channels, database settings, memberships, sessions and webhook inbox. The people (users) are kept.',
        });
        if (!ok) return;
        await api.del(`/apps/${id}`);
        toast('App deleted');
        go('#/apps');
      },
    },
    view: html`
      <div class="card">
        <h2>General</h2>
        <form data-submit="saveSettings">
          <fieldset ${write ? '' : 'disabled'}>
            ${field('Name', 'name', { value: app.name, required: true })}
            ${check('Active', 'isActive', app.isActive, 'A disabled app rejects every request.')}
            ${check('Allow self-registration', 'allowSelfRegistration', app.allowSelfRegistration, 'Turn off to let only members added by hand sign in.')}
            ${check('Sign-in only through the app’s backend', 'requireAppKeyForAuth', app.requireAppKeyForAuth,
              'Register, login, refresh and logout then need the app’s API key (X-Api-Key), so passwords are only handled by the app’s server.')}
            ${area('Redirect URIs', 'redirectUris', { value: app.redirectUris.join('\n'), hint: 'One per line. LINE Login may only send the browser back to these.' })}
            ${write ? html`<button class="btn primary">Save</button>` : ''}
          </fieldset>
        </form>
      </div>

      <div class="card">
        <h2>Identity</h2>
        <dl class="kv">
          <dt>Code (X-App-Id)</dt><dd><code>${app.code}</code></dd>
          <dt>App id</dt><dd><code>${app.id}</code></dd>
          <dt>API key</dt><dd>Stored only as a hash. ${write ? html`<button class="btn small" data-action="rotateKey">Rotate key</button>` : ''}</dd>
        </dl>
      </div>

      ${write ? html`<div class="card danger-zone">
        <h2>Danger zone</h2>
        <p class="muted">Deleting an app cannot be undone.</p>
        <button class="btn danger" data-action="deleteApp">Delete app…</button>
      </div>` : ''}`,
  };
}
