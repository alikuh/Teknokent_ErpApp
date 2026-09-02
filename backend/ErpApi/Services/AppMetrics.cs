using Prometheus;

namespace ErpApi.Services;

public static class AppMetrics
{
    public static readonly Counter LoginAttemptsTotal = Metrics.CreateCounter(
        "erp_login_attempts_total",
        "Kullanıcıların yaptığı login denemelerinin sayısı.",
        new CounterConfiguration
        {
            LabelNames = new[] { "result" }
        });

    public static readonly Gauge ActiveSessions = Metrics.CreateGauge(
        "erp_active_sessions",
        "Şu anda Redis'te aktif olduğu bilinen (login olmuş, logout/expire olmamış) oturum sayısı.");

    public static readonly Counter FailedLoginLockoutsTotal = Metrics.CreateCounter(
        "erp_failed_login_lockouts_total",
        "Çok fazla başarısız denemeden dolayı devreye giren rate-limit kilitlenmelerinin sayısı.",
        new CounterConfiguration
        {
            LabelNames = new[] { "scope" }
        });

    public static readonly Counter ReceiptsCreatedTotal = Metrics.CreateCounter(
        "erp_receipts_created_total",
        "Kaydedilen satış fişi sayısı (türüne göre: Veresiye/Nakit/Kart).",
        new CounterConfiguration
        {
            LabelNames = new[] { "type" }
        });

    public static readonly Counter PaymentsCreatedTotal = Metrics.CreateCounter(
        "erp_payments_created_total",
        "Kaydedilen tahsilat sayısı.");
}
