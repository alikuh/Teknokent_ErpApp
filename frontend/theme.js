/*
 * Siyah/beyaz tema yonetimi.
 *
 * Tema tercihi bir cerezde (cookie) tutulur - sadece bu tarayicidaki
 * tercihtir, backend'in bilmesine gerek yoktur, bu yuzden sunucuya hic
 * gonderilmesi gerekmeyen sade bir cerez olarak tutuluyor.
 */
(function () {
    "use strict";

    var COOKIE_NAME = "erp-theme";
    var COOKIE_MAX_AGE_DAYS = 365;

    function readStoredTheme() {
        var match = document.cookie.match(new RegExp("(?:^|; )" + COOKIE_NAME + "=([^;]*)"));
        return match ? decodeURIComponent(match[1]) : null;
    }

    function writeStoredTheme(theme) {
        var maxAgeSeconds = COOKIE_MAX_AGE_DAYS * 24 * 60 * 60;
        // Path=/ : sitenin tum sayfalarinda gecerli olsun.
        // SameSite=Lax : bu cerez hassas degil, ekstra kisitlamaya gerek yok.
        document.cookie = COOKIE_NAME + "=" + encodeURIComponent(theme) +
            "; Path=/; Max-Age=" + maxAgeSeconds + "; SameSite=Lax";
    }

    function prefersDark() {
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
    }

    function getTheme() {
        return readStoredTheme() || (prefersDark() ? "dark" : "light");
    }

    var SUN_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>';
    var MOON_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>';

    function updateToggleButton(theme) {
        var btn = document.getElementById("theme-toggle-btn");
        if (!btn) return;
        btn.innerHTML = theme === "dark" ? SUN_ICON : MOON_ICON;
        var label = theme === "dark" ? "Aydınlık temaya geç" : "Karanlık temaya geç";
        btn.setAttribute("aria-label", label);
        btn.title = label;
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute("data-theme", theme);
        updateToggleButton(theme);
    }

    function setTheme(theme) {
        applyTheme(theme);
        writeStoredTheme(theme);
        document.dispatchEvent(new CustomEvent("themechange", { detail: { theme: theme } }));
    }

    function toggleTheme() {
        var current = document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
        setTheme(current === "dark" ? "light" : "dark");
    }

    // FOUC'u (yanlis temayla kisa sureli goruntu) engellemek icin tema, sayfa
    // govdesi olusmadan once, bu script <head> icinde calisirken hemen uygulanir.
    applyTheme(getTheme());

    function createToggleButton() {
        if (document.getElementById("theme-toggle-btn")) return;
        var btn = document.createElement("button");
        btn.id = "theme-toggle-btn";
        btn.type = "button";
        btn.className = "theme-toggle-btn";
        btn.addEventListener("click", toggleTheme);
        document.body.appendChild(btn);
        updateToggleButton(getTheme());
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", createToggleButton);
    } else {
        createToggleButton();
    }

    window.ThemeManager = {
        getTheme: getTheme,
        setTheme: setTheme,
        toggleTheme: toggleTheme
    };
})();
