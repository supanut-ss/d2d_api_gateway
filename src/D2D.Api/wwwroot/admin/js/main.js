import * as auth from './api.js';
import {
  actions, setActions, setRerender, html, esc, field, toast, openModal, closeModal, confirmAction, submitModal, copyText, debounce,
} from './ui.js';
import { icon } from './icons.js';
import dashboard from './pages/dashboard.js';
import apps from './pages/apps.js';
import users from './pages/users.js';
import events from './pages/events.js';
import admins from './pages/admins.js';
import audit from './pages/audit.js';

const root = document.getElementById('app');

// [route key, icon name, label, page builder, sidebar section]
const NAV = [
  ['dashboard', 'dashboard', 'Dashboard', dashboard, 'Overview'],
  ['apps', 'apps', 'Apps', apps, 'Manage'],
  ['users', 'users', 'Users', users, 'Manage'],
  ['events', 'events', 'LINE events', events, 'Manage'],
  ['admins', 'admins', 'Admins', admins, 'Access'],
  ['audit', 'audit', 'Audit log', audit, 'Access'],
];
const SECTIONS = [...new Set(NAV.map((n) => n[4]))];

// Actions that work on every screen. A page's own actions are swapped in by setActions().
const globalActions = {
  __closeModal: () => closeModal(),
  __copy: (el) => copyText(el.dataset.copy),
};

let pageActions = {};
const lookup = (name) => pageActions[name] ?? globalActions[name];

async function run(fn, ...args) {
  try {
    await fn(...args);
  } catch (e) {
    toast(e.message || String(e), 'bad');
  }
}

// ---------- event delegation (no inline handlers: the CSP forbids them) ----------

// Dropdown menus (<details class="menu">) close on any click outside them or on one of their items.
function closeMenus(except) {
  document.querySelectorAll('details.menu[open]').forEach((d) => { if (d !== except) d.open = false; });
}

document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  closeMenus();
  document.body.classList.remove('nav-open');
});

document.addEventListener('click', (e) => {
  const menu = e.target.closest('details.menu');
  closeMenus(e.target.closest('details.menu [data-action]') ? null : menu);
  const el = e.target.closest('[data-action]');
  const fn = el && lookup(el.dataset.action);
  if (fn) { e.preventDefault(); run(fn, el, e); }
});

document.addEventListener('submit', (e) => {
  const name = e.target.dataset?.submit;
  if (!name) return;
  e.preventDefault();
  const data = new FormData(e.target);
  if (name === '__modal') { submitModal(data, e.target); return; }
  const fn = lookup(name);
  if (fn) run(fn, data, e.target);
});

document.addEventListener('change', (e) => {
  const el = e.target.closest('[data-change]');
  const fn = el && lookup(el.dataset.change);
  if (fn) run(fn, el, e);
});

const onSearch = debounce((el) => { const fn = lookup(el.dataset.input); if (fn) run(fn, el); }, 350);
document.addEventListener('input', (e) => {
  const el = e.target.closest('[data-input]');
  if (el) onSearch(el);
});

// ---------- routing ----------

let renderToken = 0;

function parseHash() {
  const [path, query = ''] = (location.hash || '#/dashboard').slice(1).split('?');
  return { segs: path.split('/').filter(Boolean), query: Object.fromEntries(new URLSearchParams(query)) };
}

async function renderRoute() {
  if (!auth.getSession()) return;
  const content = document.getElementById('content');
  if (!content) return;

  const { segs, query } = parseHash();
  const entry = NAV.find(([key]) => key === segs[0]);
  document.querySelectorAll('#nav a').forEach((a) => {
    if (a.dataset.nav === (entry?.[0] ?? '')) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  });
  document.body.classList.remove('nav-open');
  const crumb = document.getElementById('crumb');
  if (crumb) crumb.textContent = entry ? entry[2] : '';

  if (!entry) {
    pageActions = {};
    setActions({});
    content.innerHTML = '<div class="empty">Page not found. <a href="#/dashboard">Go to the dashboard</a></div>';
    return;
  }

  const token = ++renderToken;
  content.innerHTML = '<div class="loading">Loading…</div>';
  try {
    const page = await entry[3](segs, query);
    if (token !== renderToken) return; // the user already moved on
    pageActions = page.actions ?? {};
    setActions(pageActions);
    content.innerHTML = page.view.s;
    document.title = (page.title ? page.title + ' · ' : '') + 'D2D Console';
    page.onMount?.(content);
  } catch (e) {
    if (token !== renderToken) return;
    pageActions = {};
    content.innerHTML = html`<div class="notice bad">${e.message || e}</div>
      <button class="btn" data-action="__retry">Try again</button>`.s;
    pageActions = { __retry: () => renderRoute() };
  }
}

window.addEventListener('hashchange', renderRoute);
setRerender(renderRoute);

// ---------- shell ----------

function showApp() {
  const me = auth.getSession().admin;
  const role = me.role === 'Admin' ? 'Admin' : 'Viewer (read-only)';
  const initials = String(me.displayName || '?').trim().split(/\s+/).slice(0, 2).map((w) => w[0]).join('').toUpperCase();
  root.innerHTML = html`<div class="shell">
    <aside class="sidebar" id="sidebar">
      <div class="brand"><span class="brand-mark">D2D</span><span>Console</span></div>
      <nav class="nav" id="nav" aria-label="Main">
        ${SECTIONS.map((section) => html`<div class="nav-section">${section}</div>
          ${NAV.filter((n) => n[4] === section).map(([key, ico, label]) => html`<a href="#/${key}" data-nav="${key}">${icon(ico)}<span>${label}</span></a>`)}`)}
      </nav>
      <div class="sidebar-foot">Central API for all apps</div>
    </aside>
    <div class="scrim" data-action="shellNavClose" aria-hidden="true"></div>
    <div class="main">
      <header class="topbar">
        <button class="icon-btn nav-toggle" data-action="shellNav" aria-label="Open menu" aria-controls="sidebar">${icon('menu')}</button>
        <div class="crumb" id="crumb"></div>
        <span class="spacer"></span>
        <button class="icon-btn theme-toggle" data-action="shellTheme" aria-label="Switch light / dark" title="Switch light / dark">${icon('moon', 'i-moon')}${icon('sun', 'i-sun')}</button>
        <details class="menu">
          <summary aria-label="Account menu">
            <span class="avatar" aria-hidden="true">${initials}</span>
            <span class="who"><strong>${me.displayName}</strong><small>${role}</small></span>
            ${icon('chevron', 'chev')}
          </summary>
          <div class="menu-panel" role="menu">
            <div class="menu-head"><strong>${me.displayName}</strong><small>${role}</small></div>
            <button class="menu-item" role="menuitem" data-action="shellPassword">${icon('key')}Change password</button>
            <button class="menu-item" role="menuitem" data-action="shellSignOut">${icon('logout')}Sign out</button>
          </div>
        </details>
      </header>
      <main class="content" id="content"></main>
    </div>
  </div>`.s;
  globalActions.shellNav = () => document.body.classList.add('nav-open');
  globalActions.shellNavClose = () => document.body.classList.remove('nav-open');
  globalActions.shellTheme = toggleTheme;
  globalActions.shellPassword = changeMyPassword;
  globalActions.shellSignOut = signOut;
  if (!location.hash) location.hash = '#/dashboard';
  renderRoute();
}

function toggleTheme() {
  const dark = document.documentElement.getAttribute('data-theme') === 'dark'
    || (!document.documentElement.getAttribute('data-theme') && matchMedia('(prefers-color-scheme: dark)').matches);
  const next = dark ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', next);
  try { localStorage.setItem('d2d-theme', next); } catch { /* storage unavailable */ }
}

async function signOut() {
  await auth.logout();
  showLogin();
}

function changeMyPassword() {
  if (auth.getSession().admin.id == null) { toast('The admin key has no password to change', 'bad'); return; }
  openModal({
    title: 'Change my password',
    body: html`${field('Current password', 'current', { type: 'password', required: true, autocomplete: 'current-password' })}
      ${field('New password', 'next', { type: 'password', required: true, minlength: 10, autocomplete: 'new-password', hint: 'At least 10 characters. You will be signed out on every device.' })}`,
    onSubmit: async (fd, close) => {
      await auth.changePassword(fd.get('current'), fd.get('next'));
      close();
      toast('Password changed — please sign in again');
      showLogin();
    },
  });
}

// ---------- sign in / first-run setup ----------

async function showLogin(notice = '') {
  setActions({});
  pageActions = {};
  const setup = await auth.setupRequired();
  root.innerHTML = html`<div class="auth-wrap"><div class="auth-card">
    <div class="brand"><span class="brand-mark">D2D</span> Console</div>
    ${setup
      ? html`<h1>First-time setup</h1>
        <p class="muted">No admin exists yet. Create the first one. Proof that you own this server is the <code>Admin:ApiKey</code> from its configuration (used once, not stored).</p>
        <form data-submit="setup">
          ${field('Admin key', 'adminKey', { type: 'password', required: true })}
          ${field('Your name', 'displayName', { required: true })}
          ${field('Email', 'email', { type: 'email', required: true, autocomplete: 'username' })}
          ${field('Password', 'password', { type: 'password', required: true, minlength: 10, autocomplete: 'new-password', hint: 'At least 10 characters.' })}
          <p class="form-error" id="auth-error" hidden></p>
          <button class="btn primary">Create admin and sign in</button>
        </form>`
      : html`<h1>Sign in</h1>
        <p class="muted">Administrators only.</p>
        ${notice ? html`<div class="notice">${notice}</div>` : ''}
        <form data-submit="login">
          ${field('Email', 'email', { type: 'email', required: true, autocomplete: 'username' })}
          ${field('Password', 'password', { type: 'password', required: true, autocomplete: 'current-password' })}
          <p class="form-error" id="auth-error" hidden></p>
          <button class="btn primary">Sign in</button>
        </form>`}
  </div></div>`.s;
  document.title = 'Sign in · D2D Console';

  const fail = (e) => {
    const box = document.getElementById('auth-error');
    box.textContent = e.message || String(e);
    box.hidden = false;
  };
  globalActions.login = async (fd) => {
    try {
      await auth.login(fd.get('email'), fd.get('password'));
      showApp();
    } catch (e) { fail(e); }
  };
  globalActions.setup = async (fd) => {
    try {
      await auth.createFirstAdmin(fd.get('adminKey'), {
        email: fd.get('email'), displayName: fd.get('displayName'), password: fd.get('password'),
      });
      await auth.login(fd.get('email'), fd.get('password'));
      showApp();
    } catch (e) { fail(e); }
  };
  pageActions = {};
}

auth.onSignedOut(() => showLogin('Your session ended. Please sign in again.'));

(async function boot() {
  if (await auth.refresh()) showApp();
  else showLogin();
})();
