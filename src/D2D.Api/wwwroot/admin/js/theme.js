// Runs before the page paints so the chosen theme does not flash. Kept as a file because the CSP forbids inline scripts.
(function () {
  try {
    var saved = localStorage.getItem('d2d-theme');
    if (saved === 'light' || saved === 'dark') document.documentElement.setAttribute('data-theme', saved);
  } catch (e) { /* storage unavailable: follow the system theme */ }
})();
