import { api } from '../../api.js';
import { html, badge, yesNo, area, select, check, toast, rerender } from '../../ui.js';

export default async function database(app, write) {
  const id = app.id;
  // Admins who can edit see the stored connection string in the form (each read is recorded in the audit log).
  let currentConnection = '';
  if (write && app.databaseProvider) {
    try { currentConnection = (await api.post(`/apps/${id}/database/reveal`)).connectionString ?? ''; } catch { /* leave empty: type a new one */ }
  }
  return {
    actions: {
      saveDatabase: async (fd) => {
        await api.put(`/apps/${id}/database`, {
          provider: fd.get('provider'),
          connectionString: fd.get('connectionString'),
          allowCredentialAccess: fd.get('allowCredentialAccess') === 'on',
          verify: fd.get('verify') === 'on',
          provision: fd.get('provision') === 'on',
        });
        toast('Database saved');
        rerender();
      },
    },
    view: html`
      <div class="card">
        <h2>Current database</h2>
        <dl class="kv">
          <dt>Engine</dt><dd>${app.databaseProvider ? badge(app.databaseProvider, 'info') : badge('not configured')}</dd>
          <dt>Keys for app</dt><dd>${yesNo(app.databaseCredentialAccess, 'Allowed', 'Off')}</dd>
        </dl>
        <p class="muted">The connection string is stored encrypted. Admins who can edit see it in the form below; each view is recorded in the audit log.</p>
      </div>

      <div class="card">
        <h2>${app.databaseProvider ? 'Replace the connection' : 'Set the connection'}</h2>
        <form data-submit="saveDatabase">
          <fieldset ${write ? '' : 'disabled'}>
            ${select('Engine', 'provider', ['SqlServer', 'PostgreSql', 'MySql', 'Sqlite'], app.databaseProvider || 'SqlServer')}
            ${area('Connection string', 'connectionString', { value: currentConnection, required: true, rows: 3, placeholder: 'Server=…;Database=…;User Id=…;Password=…;' })}
            ${check('Let the app’s backend fetch it', 'allowCredentialAccess', app.databaseCredentialAccess,
              'Enables GET /api/v1/database/credentials, so the app needs no connection string of its own.')}
            ${check('Test the connection before saving', 'verify', true, 'Turn off if D2D itself cannot reach this database.')}
            ${check('Create the sample schema', 'provision', false, 'Only for modules that run inside D2D. Leave off for an app’s real database.')}
            ${write ? html`<button class="btn primary">Save</button>` : ''}
          </fieldset>
        </form>
      </div>`,
  };
}
