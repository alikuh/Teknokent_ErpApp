// Backend API adresi - tüm sayfalar bu dosyayı kullanır, tek yerden yönetilir.
//
// Backend, frontend'i kendi üzerinden de servis ediyor (bkz. Program.cs),
// bu yüzden normalde (localhost:5077 veya ngrok URL'i ile) sayfayı nereden
// açarsanız açın API_URL otomatik olarak doğru (relative "/api") olur.
//
// Sadece Live Server ile 5500 portundan (hot-reload için) geliştirirken
// frontend ve backend farklı origin'de olduğundan, o durumda API_URL
// açıkça localhost:5077'yi gösterir.
(function () {
    var isLiveServerLocal =
        (window.location.hostname === "localhost" || window.location.hostname === "127.0.0.1") &&
        window.location.port === "5500";

    window.API_URL = isLiveServerLocal ? "http://localhost:5077/api" : "/api";
})();
