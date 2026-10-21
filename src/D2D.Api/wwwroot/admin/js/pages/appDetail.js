import { api, canWrite, BASE } from '../api.js';
import {
  html, raw, badge, yesNo, fmtDate, field, area, select, check, lines, csv, qs, pager,
  openModal, confirmAction, showSecret, toast, go, rerender,
} from '../ui.js';
import { iconButton } from '../icons.js';

const TABS = [['settings', 'Settings'], ['members', 'Members'], ['channels', 'LINE channels'], ['database', 'Database']];
const PAGE = 25;

export default async function appDetail(id, tab, query) {
  const app = await api.get('/apps/' + id);
  const write = canWrite();
  const builders = { settings, members, channels, database };
  const body = await (builders[tab] ?? settings)(app, write, query);

  return {
    title: app.name,
    actions: body.actions,
    onMount: body.onMount,
    view: html`
      <div class="page-head">
        <div>
          <h1>${app.name} ${badge(app.isActive ? 'Active' : 'Disabled', app.isActive ? 'ok' : 'bad')}</h1>
          <div class="sub"><a href="#/apps">Apps</a> / <span class="mono">${app.code}</span></div>
        </div>
      </div>
      <nav class="tabs">
        ${TABS.map(([key, label]) => html`<a href="#/apps/${id}/${key}" ${key === (builders[tab] ? tab : 'settings') ? raw('aria-current="page"') : ''}>${label}</a>`)}
      </nav>
      ${write ? '' : html`<div class="notice info">You have read-only access. Ask an admin to change anything here.</div>`}
      ${body.view}`,
  };
}

// ---------- settings ----------

async function settings(app, write) {
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

// ---------- members ----------

async function members(app, write, query) {
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

// ---------- LINE channels ----------

const TOKEN_MODES = [
  ['Stateless', 'Stateless — only channel ID and secret (recommended)'],
  ['Static', 'Static — paste a long-lived token'],
  ['JwtAssertion', 'JWT assertion — v2.1 token with your own key pair'],
];

const randomKey = () => {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
};

async function channels(app, write, query = {}) {
  const id = app.id;
  const back = `#/apps/${id}/channels`;
  const url = (ch) => ch.webhookUrl || (ch.webhookPath ? location.origin + BASE + ch.webhookPath : '');
  const editing = query.edit ? app.lineChannels.find((c) => c.channelId === query.edit) : null;
  const creating = query.new === '1' || (query.edit && !editing);

  const save = async (fd) => {
    const type = fd.get('type');
    const messaging = type === 'Messaging';
    const mode = messaging ? fd.get('tokenMode') : null;
    await api.put(`/apps/${id}/line-channels`, {
      type,
      providerId: fd.get('providerId'),
      channelId: fd.get('channelId'),
      channelSecret: fd.get('channelSecret') || null,
      isActive: fd.get('isActive') === 'on',
      tokenMode: mode,
      accessToken: mode === 'Static' ? fd.get('accessToken') || null : null,
      assertionKeyId: mode === 'JwtAssertion' ? fd.get('assertionKeyId') : null,
      assertionPrivateKey: mode === 'JwtAssertion' ? fd.get('assertionPrivateKey') || null : null,
      webhookForwardUrl: messaging ? fd.get('webhookForwardUrl') || null : null,
      liffId: fd.get('liffId') || null,
      allowCredentialAccess: fd.get('allowCredentialAccess') === 'on',
      jobsKey: fd.get('jobsKey') || null,
    });
    toast('Channel saved');
    go(back);
  };

  const actions = {
    saveChannel: save,
    cancelChannel: () => go(back),
    generateJobsKey: () => {
      const input = document.getElementById('f-jobsKey');
      input.type = 'text';
      input.value = randomKey();
      input.focus();
    },
    verifyChannel: async (el) => {
      el.setAttribute('aria-busy', 'true');
      try {
        const bot = await api.post(`/apps/${id}/line-channels/${el.dataset.id}/verify`);
        openModal({
          title: 'Channel works', noSubmit: true,
          body: html`<p>D2D obtained an access token and LINE answered:</p>
            <dl class="kv"><dt>Bot name</dt><dd>${bot?.displayName || '—'}</dd><dt>Basic ID</dt><dd>${bot?.basicId || '—'}</dd><dt>Bot user ID</dt><dd><code>${bot?.userId || '—'}</code></dd></dl>`,
        });
      } finally {
        el.removeAttribute('aria-busy');
      }
    },
    deleteChannel: async (el) => {
      const ok = await confirmAction({
        title: 'Delete channel', danger: true, confirmLabel: 'Delete',
        message: `Remove channel ${el.dataset.id}? Its stored keys are deleted and its webhook URL stops working.`,
      });
      if (!ok) return;
      await api.del(`/apps/${id}/line-channels/${el.dataset.id}`);
      toast('Channel deleted');
      rerender();
    },
  };

  // Shows only the groups that matter for the chosen type and token mode.
  const onMount = (root) => {
    const form = root.querySelector('form[data-submit="saveChannel"]');
    if (!form) return;
    const sync = () => {
      const type = form.querySelector('[name="type"]').value;
      const mode = form.querySelector('[name="tokenMode"]')?.value;
      form.querySelectorAll('.grp-messaging').forEach((el) => { el.hidden = type !== 'Messaging'; });
      form.querySelectorAll('.grp-login').forEach((el) => { el.hidden = type !== 'Login'; });
      form.querySelectorAll('.grp-static').forEach((el) => { el.hidden = mode !== 'Static'; });
      form.querySelectorAll('.grp-jwt').forEach((el) => { el.hidden = mode !== 'JwtAssertion'; });
    };
    sync();
    form.querySelectorAll('[name="type"], [name="tokenMode"]').forEach((el) => el.addEventListener('change', sync));
  };

  // ----- one page: provider first, then the channel, then what depends on its type, then what the app's backend may read -----
  if (creating || editing) {
    const ch = editing;
    const isEdit = !!ch;
    const mode = ch?.tokenMode || 'Stateless';
    const providers = [...new Set(app.lineChannels.map((c) => c.providerId))];
    const keep = (hint) => (isEdit ? `${hint} Leave blank to keep the stored value.` : hint);

    return {
      actions,
      onMount,
      view: html`
        <div class="row mb"><a href="${back}">← LINE channels</a></div>
        <form data-submit="saveChannel">
          <fieldset ${write ? '' : 'disabled'}>
            <div class="card">
              <h2>1. Provider</h2>
              ${field('LINE provider ID', 'providerId', { value: ch?.providerId || providers[0] || '', required: true, readonly: isEdit,
                hint: isEdit ? 'Fixed once the channel is registered.' : 'From the LINE console URL …/provider/<id>. Channels of one provider share user IDs, so a bot and its LIFF app must use the same one.' })}
            </div>

            <div class="card">
              <h2>2. Channel</h2>
              ${isEdit
                ? html`<input type="hidden" name="type" value="${ch.type}">${field('Type', 'typeShown', { value: ch.type, readonly: true })}`
                : select('Type', 'type', [['Messaging', 'Messaging API (bot, push, webhook)'], ['Login', 'LINE Login (LIFF, web login)']], query.type === 'Login' ? 'Login' : 'Messaging')}
              <div class="grid-2">
                ${field('Channel ID', 'channelId', { value: ch?.channelId || '', required: true, readonly: isEdit })}
                ${field('Channel secret', 'channelSecret', { type: 'password', required: !isEdit, autocomplete: 'new-password',
                  hint: isEdit ? 'Stored encrypted and never shown. Leave blank to keep it.' : '' })}
              </div>
              ${check('Channel is active', 'isActive', ch ? ch.isActive : true, 'An inactive channel is ignored: its webhook is refused and its keys are not handed out.')}
            </div>

            <div class="card grp-messaging">
              <h2>3. Messaging API</h2>
              ${select('Access token', 'tokenMode', TOKEN_MODES, mode)}
              <div class="grp-static">${field('Access token', 'accessToken', { type: 'password', autocomplete: 'new-password',
                hint: keep('The long-lived token from the LINE console.') })}</div>
              <div class="grp-jwt">
                ${field('Key ID (kid)', 'assertionKeyId', { value: ch?.assertionKeyId || '', hint: 'Returned by LINE when you registered the public key.' })}
                ${area('Private key (JWK JSON)', 'assertionPrivateKey', { rows: 5, hint: keep('The matching private key. Stored encrypted and never returned.') })}
              </div>
              ${isEdit ? html`<div class="field"><label>Webhook URL (paste into LINE)</label>
                <div><code>${url(ch)}</code> <button type="button" class="btn small ghost" data-action="__copy" data-copy="${url(ch)}">Copy</button></div></div>` : ''}
              ${field('Forward events to', 'webhookForwardUrl', { type: 'url', value: ch?.webhookForwardUrl || '', placeholder: 'https://api.example.com/payyet/line/webhook',
                hint: 'Optional. Each event is re-sent there, signed with the channel secret, so the app’s own LINE SDK code keeps working.' })}
            </div>

            <div class="card grp-login">
              <h2>3. LINE Login / LIFF</h2>
              ${field('LIFF ID', 'liffId', { value: ch?.liffId || '', placeholder: '2012345678-AbCdEfGh',
                hint: 'From the LIFF tab of this Login channel. Not secret; the app reads it with the other LINE settings.' })}
            </div>

            <div class="card">
              <h2>4. App backend access</h2>
              ${check('Let the app’s backend fetch these settings', 'allowCredentialAccess', ch?.allowCredentialAccess || false,
                'Enables GET /api/v1/line/credentials/all (and …/channels/{id}/credentials): provider, channel secret, access token, LIFF ID, jobs key and forward URL.')}
              <div class="row">
                ${field('Jobs key', 'jobsKey', { type: 'password', autocomplete: 'new-password',
                  hint: `${isEdit ? (ch.hasJobsKey ? 'A key is stored. ' : 'No key stored yet. ') : ''}Guards the app’s scheduled-job endpoints (/jobs/*). Stored encrypted; handed to the app only when access above is on. Leave blank to keep it.` })}
                <button type="button" class="btn small" data-action="generateJobsKey">Generate</button>
              </div>
            </div>

            ${isEdit ? html`<div class="card">
              <h2>Status</h2>
              <dl class="kv">
                <dt>Last changed</dt><dd>${fmtDate(ch.updatedAt)}</dd>
                <dt>Issued token</dt><dd>${ch.issuedTokenExpiresAt ? 'valid until ' + fmtDate(ch.issuedTokenExpiresAt) : '—'}</dd>
              </dl>
            </div>` : ''}

            ${write ? html`<div class="row"><button class="btn primary">Save</button> <button type="button" class="btn" data-action="cancelChannel">Cancel</button></div>` : ''}
          </fieldset>
        </form>`,
    };
  }

  // ----- the list -----
  return {
    actions,
    view: html`
      <div class="row end mb">
        ${write ? html`<a class="btn primary" href="${back}?new=1">Add channel</a>` : ''}
      </div>
      ${app.lineChannels.length === 0
        ? html`<div class="card empty">No LINE channel yet. Add the Messaging API channel of the bot and/or the LINE Login channel of the LIFF app.</div>`
        : html`<div class="table-wrap"><table>
            <thead><tr><th>Channel</th><th>Type</th><th>Webhook URL (paste into LINE) / LIFF ID</th><th>App access</th><th></th></tr></thead>
            <tbody>${app.lineChannels.map((ch) => html`<tr>
              <td><div class="stack"><code>${ch.channelId}</code><span class="muted">provider ${ch.providerId}</span>${ch.isActive ? '' : badge('Inactive', 'bad')}</div></td>
              <td><div class="stack">${badge(ch.type, ch.type === 'Messaging' ? 'info' : 'warn')}
                ${ch.type === 'Messaging' ? html`<span class="muted">token: ${ch.tokenMode}</span>` : ''}
                ${ch.issuedTokenExpiresAt ? html`<span class="muted">issued token until ${fmtDate(ch.issuedTokenExpiresAt)}</span>` : ''}</div></td>
              <td>${ch.type === 'Messaging'
                ? html`<div class="url-cell"><code class="url">${url(ch)}</code>${iconButton('copy', 'Copy webhook URL', { data: { action: '__copy', copy: url(ch) } })}</div>
                    ${ch.webhookForwardUrl ? html`<span class="muted url">forwards to ${ch.webhookForwardUrl}</span>` : ''}`
                : ch.liffId ? html`<span class="muted">LIFF ID</span> <code>${ch.liffId}</code>` : '—'}</td>
              <td><div class="stack">
                <span><span class="muted">Keys</span> ${yesNo(ch.allowCredentialAccess, 'Allowed', 'Off')}</span>
                <span><span class="muted">Jobs key</span> ${yesNo(ch.hasJobsKey, 'Set', 'Not set')}</span>
              </div></td>
              <td class="actions">
                ${ch.type === 'Messaging' && write ? iconButton('zap', 'Test channel', { data: { action: 'verifyChannel', id: ch.channelId } }) : ''}
                ${write ? html`${iconButton('edit', 'Edit channel', { href: `${back}?edit=${ch.channelId}` })}
                  ${iconButton('trash', 'Delete channel', { danger: true, data: { action: 'deleteChannel', id: ch.channelId } })}` : ''}
              </td>
            </tr>`)}</tbody>
          </table></div>`}`,
  };
}

// ---------- database ----------

async function database(app, write) {
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
