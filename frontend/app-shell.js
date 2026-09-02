/*
 * Uygulama kabuğu: her korunan sayfa bunu yükler.
 *  - Oturum yoksa login'e atar.
 *  - Sol menüyü çizer, aktif sayfayı işaretler, "Açık Veresiye" KPI'sını doldurur.
 *  - authFetch (Authorization + CSRF + 401 yakalama) ve biçim yardımcılarını verir.
 */
(function () {
  "use strict";

  var API = window.API_URL;
  var token = sessionStorage.getItem("token");
  var username = sessionStorage.getItem("username");

  if (!token) {
    window.location.replace("login.html");
    return;
  }

  // ─── Biçimlendirme ───────────────────────────────────────────────
  var trMoney = new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  var trQty = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 2 });

  function money(n) { return trMoney.format(Number(n) || 0) + " ₺"; }
  function qty(n) { return trQty.format(Number(n) || 0); }

  function dtext(iso) {
    if (!iso) return "—";
    var p = String(iso).slice(0, 10).split("-");
    return p[2] + "." + p[1] + "." + p[0];
  }
  function ageDays(iso) {
    if (!iso) return 0;
    var d = new Date(String(iso).slice(0, 10) + "T00:00:00");
    return Math.max(0, Math.round((Date.now() - d.getTime()) / 86400000));
  }
  function todayIso() { return new Date().toISOString().slice(0, 10); }
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  // ─── authFetch ───────────────────────────────────────────────────
  var sessionExpiredShown = false;

  function showSessionExpired() {
    if (sessionExpiredShown) return;
    sessionExpiredShown = true;
    var ov = document.getElementById("session-expired-overlay");
    if (ov) ov.classList.add("open");
    else { alert("Oturum süresi doldu. Tekrar giriş yapın."); goLogin(); }
  }

  async function authFetch(url, options) {
    var opts = Object.assign({}, options || {});
    opts.credentials = "include";
    opts.headers = Object.assign({}, (options && options.headers) || {}, { Authorization: token });

    var method = (opts.method || "GET").toUpperCase();
    if (method !== "GET" && method !== "HEAD") {
      opts.headers["X-CSRF-Token"] = window.CsrfHelper ? window.CsrfHelper.getToken() : "";
    }

    var res = await fetch(url, opts);
    if (res.status === 401) { showSessionExpired(); return null; }
    return res;
  }

  async function apiJson(path, options) {
    var res = await authFetch(API + path, options);
    if (!res) return null;
    if (!res.ok) {
      var text = await res.text().catch(function () { return ""; });
      var err = new Error(text || res.statusText);
      err.status = res.status;
      throw err;
    }
    var ct = res.headers.get("content-type") || "";
    return ct.indexOf("application/json") >= 0 ? res.json() : null;
  }

  function goLogin() {
    sessionStorage.clear();
    window.location.replace("login.html");
  }

  async function logout() {
    try {
      await fetch(API + "/users/logout", { method: "POST", headers: { Authorization: token } });
    } catch (e) { /* sunucuya ulaşılamasa da istemci çıkışı yapılır */ }
    goLogin();
  }

  // ─── Sol menü ────────────────────────────────────────────────────
  var NAV = [
    { href: "dashboard.html", label: "Panel" },
    { href: "satis.html", label: "Yeni Satış" },
    { href: "veresiye.html", label: "Veresiye Defteri", badge: "debt" },
    { href: "musteriler.html", label: "Müşteriler" },
    { href: "urunler.html", label: "Ürün & Stok", badge: "warn" },
    { href: "tahsilat.html", label: "Tahsilat" },
    { href: "raporlar.html", label: "Raporlar" }
  ];

  function currentPage() {
    var p = window.location.pathname.split("/").pop() || "dashboard.html";
    if (p === "" || p === "index.html") return "dashboard.html";
    if (p === "musteri.html") return "musteriler.html";
    return p;
  }

  function renderSidebar() {
    var aside = document.getElementById("app-nav");
    if (!aside) return;
    var here = currentPage();
    var isDev = /^(localhost|127\.0\.0\.1)/.test(window.location.hostname);

    aside.className = "sidebar";
    aside.innerHTML =
      '<div class="sidebar-brand">' +
        '<div class="sidebar-kicker">Veresiye Defteri</div>' +
        '<div class="sidebar-title">' + esc(username || "Dükkân") + "</div>" +
      "</div>" +
      '<nav class="sidebar-nav">' +
        NAV.map(function (n) {
          var active = n.href === here ? ' aria-current="page"' : "";
          var badge = n.badge
            ? '<span class="badge ' + n.badge + '" data-badge="' + n.badge + '" hidden></span>'
            : "";
          return '<a href="' + n.href + '"' + active + "><span>" + esc(n.label) + "</span>" + badge + "</a>";
        }).join("") +
      "</nav>" +
      '<div class="sidebar-foot">' +
        '<div class="sidebar-kpi"><div class="label">Açık Veresiye</div>' +
          '<div class="value" id="sidebar-open-debt">—</div></div>' +
        (isDev ? '<button class="sidebar-btn" id="sidebar-seed">Örnek veriyi yükle</button>' : "") +
        '<div class="sidebar-user">' + esc(username || "") +
          ' · <a href="#" id="sidebar-logout">Çıkış</a></div>' +
      "</div>";

    document.getElementById("sidebar-logout").addEventListener("click", function (e) {
      e.preventDefault(); logout();
    });
    var seed = document.getElementById("sidebar-seed");
    if (seed) seed.addEventListener("click", seedSampleData);
  }

  async function refreshShellStats() {
    try {
      var s = await apiJson("/dashboard/summary");
      if (!s) return;
      var el = document.getElementById("sidebar-open-debt");
      if (el) el.textContent = money(s.openReceivables);

      var warn = document.querySelector('[data-badge="warn"]');
      if (warn && s.criticalStockCount > 0) { warn.textContent = s.criticalStockCount; warn.hidden = false; }
    } catch (e) { /* KPI dolmazsa sayfa yine çalışır */ }

    try {
      var open = await apiJson("/receipts");
      var debt = document.querySelector('[data-badge="debt"]');
      if (open && debt && open.count > 0) { debt.textContent = open.count; debt.hidden = false; }
    } catch (e) { /* yoksay */ }
  }

  async function seedSampleData() {
    if (!confirm("Mevcut defter silinip örnek veriyle doldurulacak. Devam?")) return;
    try {
      await apiJson("/dev/seed", { method: "POST" });
      window.location.reload();
    } catch (e) {
      alert("Örnek veri yüklenemedi: " + e.message);
    }
  }

  // ─── Dışa aç ─────────────────────────────────────────────────────
  window.Shell = {
    api: API,
    token: token,
    username: username,
    authFetch: authFetch,
    apiJson: apiJson,
    money: money,
    qty: qty,
    dtext: dtext,
    ageDays: ageDays,
    todayIso: todayIso,
    esc: esc,
    logout: logout,
    refreshShellStats: refreshShellStats
  };

  document.addEventListener("DOMContentLoaded", function () {
    renderSidebar();
    refreshShellStats();
  });
})();
