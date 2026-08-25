/*
 * Siyah/beyaz tema yonetimi.
 *
 * NOT: Tema tercihi su an localStorage'da tutuluyor. Ilerleyen adimda
 * olusturulacak cerez (cookie) altyapisina gecildiginde sadece
 * readStoredTheme/writeStoredTheme fonksiyonlarinin icini degistirmek yeterli
 * - sayfalardaki diger kodun degismesine gerek yok.
 */
(function () {
    "use strict";

    var STORAGE_KEY = "erp-theme";

    function readStoredTheme() {
        try {
            return localStorage.getItem(STORAGE_KEY);
        } catch (e) {
            return null;
        }
    }

    function writeStoredTheme(theme) {
        try {
            localStorage.setItem(STORAGE_KEY, theme);
        } catch (e) {
            // localStorage kullanilamiyorsa (ör. gizli sekme) sessizce yok say;
            // tema o oturum icin sayfa yenilenene kadar gecerli kalir.
        }
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
