import { api, canWrite } from '../api.js';
import { html, raw, badge } from '../ui.js';
import settings from './appTabs/settings.js';
import members from './appTabs/members.js';
import channels from './appTabs/channels.js';
import database from './appTabs/database.js';

const TABS = [['settings', 'Settings'], ['members', 'Members'], ['channels', 'LINE channels'], ['database', 'Database']];
const BUILDERS = { settings, members, channels, database };

export default async function appDetail(id, tab, query) {
  const app = await api.get('/apps/' + id);
  const write = canWrite();
  const body = await (BUILDERS[tab] ?? settings)(app, write, query);

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
        ${TABS.map(([key, label]) => html`<a href="#/apps/${id}/${key}" ${key === (BUILDERS[tab] ? tab : 'settings') ? raw('aria-current="page"') : ''}>${label}</a>`)}
      </nav>
      ${write ? '' : html`<div class="notice info">You have read-only access. Ask an admin to change anything here.</div>`}
      ${body.view}`,
  };
}
