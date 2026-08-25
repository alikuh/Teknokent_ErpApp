/*
 * CSRF token yardimcisi ("double submit cookie" yontemi).
 *
 * Backend (bkz. Middleware/CsrfMiddleware.cs) her istekte, yoksa bir
 * csrf_token cerezi olusturuyor. Biz bu cerezin degerini okuyup, durum
 * degistiren her istekte (POST/PUT/DELETE) X-CSRF-Token header'i olarak
 * geri gonderiyoruz; backend ikisinin ayni oldugunu kontrol ediyor.
 *
 * Cerezin sunucudan gelebilmesi icin fetch cagrilarina credentials:"include"
 * eklenmesi sart (frontend farkli porttan servis edildigi icin, aksi halde
 * tarayici cerezi ne gonderir ne de kabul eder).
 */
(function () {
    "use strict";

    var COOKIE_NAME = "csrf_token";

    function getToken() {
        var match = document.cookie.match(new RegExp("(?:^|; )" + COOKIE_NAME + "=([^;]*)"));
        return match ? decodeURIComponent(match[1]) : null;
    }

    // Login/register gibi, kullanicinin ilk API cagrisinin dogrudan bir POST
    // oldugu sayfalarda, o POST'tan once cerezin var olmasini saglamak icin
    // cagirilir - aksi halde ilk POST'ta gonderilecek bir token bulunamaz.
    function prime(apiUrl) {
        return fetch(apiUrl + "/csrf-token", { credentials: "include" }).catch(function () {
            // Isindirma cagrisi basarisiz olsa bile normal akis devam etmeli;
            // ilk POST bu durumda 403 donerse kullanici tekrar dener.
        });
    }

    window.CsrfHelper = { getToken: getToken, prime: prime };
})();
