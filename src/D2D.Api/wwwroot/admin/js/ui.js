// Small UI toolkit: auto-escaping templates, form helpers, modal, toast, pager.
// Every value put into html`...` is escaped unless it is already a Safe (a nested html`...`).

export class Safe {
  constructor(s) { this.s = s; }
  toString() { return this.s; }
}

const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
export const esc = (v) => String(v).replace(/[&<>"']/g, (c) => ESC[c]);

/** Marks markup as already safe (only for fixed strings written here, with values passed through esc()). */
export const raw = (s) => new Safe(s);

function part(v) {
  if (v == null || v === false) return '';
  if (v instanceof Safe) return v.s;
  if (Array.isArray(v)) return v.map(part).join('');
  return esc(v);
}

export function html(strings, ...values) {
  let out = strings[0];
  for (let i = 0; i < values.length; i++) out += part(values[i]) + strings[i + 1];
  return new Safe(out);
}

// ---------- formatting ----------

export function fmtDate(value) {
  if (!value) return '—';
  // The API sends UTC; SQL providers may drop the "Z", which would make the browser read it as local time.
  const text = /(Z|[+-]\d\d:?\d\d)$/.test(value) ? value : value + 'Z';
  const d = new Date(text);
  return Number.isNaN(d.getTime()) ? String(value) : d.toLocaleString();
}

export function badge(text, kind = '') {
  return html`<span class="badge ${kind}">${text}</span>`;
}

export const yesNo = (v, yes = 'Yes', no = 'No') => badge(v ? yes : no, v ? 'ok' : '');

export function shortId(id) { return String(id).slice(0, 8); }

// ---------- form fields ----------

export function field(label, name, { type = 'text', value = '', required = false, hint = '', placeholder = '', readonly = false, minlength, pattern, autocomplete = 'off' } = {}) {
  return html`<div class="field">
    <label for="f-${name}">${label}${required ? ' *' : ''}</label>
    <input id="f-${name}" name="${name}" type="${type}" value="${value}" placeholder="${placeholder}" autocomplete="${autocomplete}"
      ${required ? 'required' : ''} ${readonly ? 'readonly' : ''} ${minlength ? raw(`minlength="${esc(minlength)}"`) : ''} ${pattern ? raw(`pattern="${esc(pattern)}"`) : ''}>
    ${hint ? html`<span class="hint">${hint}</span>` : ''}
  </div>`;
}

export function area(label, name, { value = '', required = false, hint = '', rows = 4, placeholder = '' } = {}) {
  return html`<div class="field">
    <label for="f-${name}">${label}${required ? ' *' : ''}</label>
    <textarea id="f-${name}" name="${name}" rows="${rows}" placeholder="${placeholder}" ${required ? 'required' : ''}>${value}</textarea>
    ${hint ? html`<span class="hint">${hint}</span>` : ''}
  </div>`;
}

export function select(label, name, options, value = '', { hint = '', disabled = false } = {}) {
  return html`<div class="field">
    <label for="f-${name}">${label}</label>
    <select id="f-${name}" name="${name}" ${disabled ? 'disabled' : ''}>
      ${options.map((o) => {
        const [v, text] = Array.isArray(o) ? o : [o, o];
        return html`<option value="${v}" ${v === value ? 'selected' : ''}>${text}</option>`;
      })}
    </select>
    ${hint ? html`<span class="hint">${hint}</span>` : ''}
  </div>`;
}

export function check(label, name, checked = false, hint = '') {
  return html`<label class="check">
    <input type="checkbox" name="${name}" ${checked ? 'checked' : ''}>
    <span>${label}${hint ? html`<span class="hint">${hint}</span>` : ''}</span>
  </label>`;
}

export const lines = (text) => String(text || '').split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
export const csv = (text) => String(text || '').split(',').map((s) => s.trim()).filter(Boolean);

// ---------- pager ----------

export function pager({ skip, take, total }, action = 'page') {
  if (total <= take) return html`<div class="pager"><span>${total} total</span></div>`;
  const from = total === 0 ? 0 : skip + 1;
  const to = Math.min(skip + take, total);
  return html`<div class="pager">
    <span>${from}–${to} of ${total}</span>
    <span class="row">
      <button class="btn small" data-action="${action}" data-skip="${Math.max(0, skip - take)}" ${skip === 0 ? 'disabled' : ''}>Previous</button>
      <button class="btn small" data-action="${action}" data-skip="${skip + take}" ${skip + take >= total ? 'disabled' : ''}>Next</button>
    </span>
  </div>`;
}

// ---------- actions (event delegation) ----------

export const actions = {};
export function setActions(map) {
  for (const k of Object.keys(actions)) delete actions[k];
  Object.assign(actions, map);
}

// ---------- toast ----------

export function toast(message, kind = '') {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.textContent = message;
  document.getElementById('toasts').append(el);
  setTimeout(() => el.remove(), kind === 'bad' ? 7000 : 3500);
}

// ---------- modal ----------

let modalSubmit = null;
let onModalClosed = null;

export function closeModal() {
  const d = document.getElementById('modal');
  if (d.open) d.close();
}

/**
 * Opens the shared dialog. onSubmit(formData, close) may be async; throwing shows the message inside the dialog.
 * onOpen(dialogElement) runs after it is shown (to wire small behaviours like showing/hiding fields).
 */
export function openModal({ title, body, submitLabel = 'Save', danger = false, wide = false, cancel = true, noSubmit = false, onSubmit, onOpen }) {
  const d = document.getElementById('modal');
  d.className = wide ? 'wide' : '';
  d.innerHTML = html`<form data-submit="__modal" autocomplete="off">
    <header><h2>${title}</h2><button type="button" class="icon-btn" data-action="__closeModal" aria-label="Close">×</button></header>
    <div class="modal-body">${body}<p class="form-error" id="modal-error" hidden></p></div>
    <footer>
      ${cancel ? html`<button type="button" class="btn" data-action="__closeModal">${noSubmit ? 'Close' : 'Cancel'}</button>` : ''}
      ${noSubmit ? '' : html`<button class="btn ${danger ? 'danger' : 'primary'}">${submitLabel}</button>`}
    </footer>
  </form>`.s;
  modalSubmit = onSubmit;
  if (!d.open) d.showModal();
  onOpen?.(d);
  d.querySelector('input:not([readonly]):not([type=checkbox]), textarea, select')?.focus();
}

export function showModalError(message) {
  const el = document.getElementById('modal-error');
  if (!el) return;
  el.textContent = message;
  el.hidden = false;
}

export async function submitModal(formData, form) {
  if (!modalSubmit) return;
  const button = form.querySelector('footer .primary, footer .danger');
  button?.setAttribute('aria-busy', 'true');
  document.getElementById('modal-error').hidden = true;
  try {
    await modalSubmit(formData, closeModal);
  } catch (e) {
    showModalError(e.message || String(e));
  } finally {
    button?.removeAttribute('aria-busy');
  }
}

/** Asks the user to confirm. Resolves true on confirm, false when cancelled/closed. */
export function confirmAction({ title, message, confirmLabel = 'Confirm', danger = false, typeToConfirm = null }) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (value) => { if (!done) { done = true; resolve(value); } };
    const d = document.getElementById('modal');
    d.addEventListener('close', () => finish(false), { once: true });
    openModal({
      title,
      danger,
      submitLabel: confirmLabel,
      body: html`<p>${message}</p>
        ${typeToConfirm ? field(`Type “${typeToConfirm}” to confirm`, 'confirmText', { required: true, autocomplete: 'off' }) : ''}`,
      onSubmit: async (fd, close) => {
        if (typeToConfirm && fd.get('confirmText') !== typeToConfirm) throw new Error('The text does not match.');
        finish(true);
        close();
      },
    });
  });
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('Copied');
  } catch {
    toast('Copy failed — select the text and copy it manually', 'bad');
  }
}

/** Shows a value that will never be shown again (API keys). */
export function showSecret(title, intro, secret) {
  openModal({
    title,
    noSubmit: true,
    body: html`<div class="notice">${intro}</div>
      <code class="secret">${secret}</code>
      <button type="button" class="btn" data-action="__copy" data-copy="${secret}">Copy</button>`,
  });
}

export function debounce(fn, ms = 300) {
  let t;
  return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
}

// ---------- navigation helpers ----------

let rerenderFn = () => {};
export const setRerender = (fn) => { rerenderFn = fn; };
export const rerender = () => rerenderFn();

/** Changes the hash route; re-renders in place when it is already there. */
export function go(hash) {
  if (location.hash === hash) rerenderFn();
  else location.hash = hash;
}

/** Builds "?a=1&b=2" from an object, leaving out empty values. */
export function qs(params) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') p.set(k, v);
  const s = p.toString();
  return s ? '?' + s : '';
}
