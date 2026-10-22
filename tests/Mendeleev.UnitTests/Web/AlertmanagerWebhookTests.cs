using System.Text.Json;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Web.Endpoints.Internal;

namespace Mendeleev.UnitTests.Web
{
    /// <summary>ТЗ 30, «Алерты»: Prometheus alerts reach the alert bot as the service's own do.</summary>
    public class AlertmanagerWebhookTests
    {
        // Alertmanager's webhook payload, version 4, trimmed to the fields that are always there.
        private const string Payload = """
            {
              "version": "4",
              "status": "firing",
              "receiver": "mendeleev",
              "groupLabels": { "alertname": "NoSuccessfulPayments" },
              "alerts": [
                {
                  "status": "firing",
                  "labels": { "alertname": "NoSuccessfulPayments", "severity": "critical", "audience": "admin" },
                  "annotations": { "summary": "За 3 часа ни одной успешной оплаты.", "description": "Проверьте агрегатор." },
                  "startsAt": "2026-10-22T07:00:00Z",
                  "endsAt": "0001-01-01T00:00:00Z",
                  "fingerprint": "a1b2c3"
                },
                {
                  "status": "resolved",
                  "labels": { "alertname": "DiskSpaceLow", "severity": "warning", "node": "DE1" },
                  "annotations": { "summary": "На DE1 свободно 9 % диска /." },
                  "startsAt": "2026-10-22T06:00:00Z",
                  "endsAt": "2026-10-22T06:30:00Z",
                  "fingerprint": "d4e5f6"
                },
                {
                  "status": "firing",
                  "labels": { "alertname": "Custom" },
                  "annotations": {},
                  "fingerprint": "070809"
                }
              ]
            }
            """;

        [Fact]
        public void Alerts_KeepSeverityAudienceAndText_AndAResolvedOneIsInfoWithItsOwnKey()
        {
            var notification = JsonSerializer.Deserialize<AlertmanagerWebhook.Notification>(Payload, JsonSerializerOptions.Web);

            IReadOnlyList<Alert> alerts = AlertmanagerWebhook.ToAlerts(notification);

            alerts.Count.ShouldBe(3);
            alerts[0].ShouldBe(new Alert(
                AlertSeverity.Critical,
                "prometheus:NoSuccessfulPayments:a1b2c3:firing",
                "За 3 часа ни одной успешной оплаты.\nПроверьте агрегатор.",
                AlertAudience.TechAdminAndAdmin));
            alerts[1].ShouldBe(new Alert(
                AlertSeverity.Info,
                "prometheus:DiskSpaceLow:d4e5f6:resolved",
                "✅ Решено: На DE1 свободно 9 % диска /.",
                AlertAudience.TechAdmin));
            alerts[2].ShouldBe(new Alert(AlertSeverity.Info, "prometheus:Custom:070809:firing", "Custom", AlertAudience.TechAdmin));
        }

        [Fact]
        public void AnEmptyNotification_RaisesNothing()
        {
            AlertmanagerWebhook.ToAlerts(null).ShouldBeEmpty();
            AlertmanagerWebhook.ToAlerts(new AlertmanagerWebhook.Notification(null)).ShouldBeEmpty();
        }
    }
}
