using Prometheus;

namespace ErpApi.Services;

/// <summary>
/// Uygulamaya özel Prometheus metrikleri burada tek yerde tanımlanır.
/// prometheus-net.AspNetCore zaten HTTP istek sayısı/süresi gibi metrikleri
/// otomatik topluyor (bkz. Program.cs -> UseHttpMetrics); buradakiler ise
/// "iş" seviyesinde anlam taşıyan, elle işaretlenen metriklerdir.
///
/// Not: Prometheus label'larına asla kullanıcı adı, IP gibi yüksek-cardinality
/// (çok farklı değer alabilen) veriler koyulmaz - her farklı değer ayrı bir
/// zaman serisi demektir ve Prometheus'u yavaşlatır/şişirir. Kişiye özel bilgi
/// bu yüzden Loki (log) tarafında tutulur.
/// </summary>
public static class AppMetrics
{
    public static readonly Counter LoginAttemptsTotal = Metrics.CreateCounter(
        "erp_login_attempts_total",
        "Kullanıcıların yaptığı login denemelerinin sayısı.",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" } // "success" | "failure"
        });

    public static readonly Gauge ActiveSessions = Metrics.CreateGauge(
        "erp_active_sessions",
        "Şu anda Redis'te aktif olduğu bilinen (login olmuş, logout/expire olmamış) oturum sayısı.");

    public static readonly Counter FailedLoginLockoutsTotal = Metrics.CreateCounter(
        "erp_failed_login_lockouts_total",
        "Çok fazla başarısız denemeden dolayı devreye giren rate-limit kilitlenmelerinin sayısı.",
        new CounterConfiguration
        {
            LabelNames = new[] { "scope" } // "user" | "ip"
        });
}
