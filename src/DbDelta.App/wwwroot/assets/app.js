/* =============================================================================
   DbDelta — Design System docs page JS
   ============================================================================= */
(function () {
  "use strict";

  /* ---------- Theme handling ---------- */
  const root = document.documentElement;
  const stored = localStorage.getItem("dbdelta-theme");
  if (stored === "light" || stored === "dark") {
    root.setAttribute("data-theme", stored);
  } else {
    root.setAttribute("data-theme", "light");
  }

  document.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-theme-set]");
    if (!btn) return;
    const t = btn.getAttribute("data-theme-set");
    root.setAttribute("data-theme", t);
    localStorage.setItem("dbdelta-theme", t);
    refreshThemeToggleUI();
  });

  function refreshThemeToggleUI() {
    const current = root.getAttribute("data-theme");
    document.querySelectorAll("[data-theme-set]").forEach((b) => {
      b.classList.toggle("is-active", b.getAttribute("data-theme-set") === current);
    });
  }

  /* ---------- TOC active link tracking ---------- */
  function setupTocTracking() {
    const sections = document.querySelectorAll(".ds-section[id], #intro");
    const links = document.querySelectorAll(".ds-toc-link[href^='#']");
    if (!sections.length || !links.length) return;

    const linkById = new Map();
    links.forEach((l) => linkById.set(l.getAttribute("href").slice(1), l));

    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            links.forEach((l) => l.classList.remove("is-active"));
            const l = linkById.get(entry.target.id);
            if (l) l.classList.add("is-active");
          }
        });
      },
      { rootMargin: "-30% 0px -60% 0px", threshold: 0 }
    );
    sections.forEach((s) => io.observe(s));
  }

  /* ---------- Copy buttons ---------- */
  document.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-copy]");
    if (!btn) return;
    const sel = btn.getAttribute("data-copy");
    const el = document.querySelector(sel);
    if (!el) return;
    const txt = el.innerText.trim();
    navigator.clipboard?.writeText(txt);
    const orig = btn.innerHTML;
    btn.innerHTML = "Copied";
    setTimeout(() => { btn.innerHTML = orig; }, 1200);
  });

  /* ---------- Checkbox-like toggle (for option rows) ---------- */
  document.addEventListener("click", (e) => {
    const t = e.target.closest("[data-toggle-check]");
    if (!t) return;
    const cur = t.getAttribute("aria-checked");
    t.setAttribute("aria-checked", cur === "true" ? "false" : "true");
  });

  /* ---------- Init ---------- */
  function init() {
    refreshThemeToggleUI();
    setupTocTracking();
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
