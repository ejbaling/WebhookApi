using System;

namespace WebhookApi.Services
{
    public record TimeData(double timeZone, string date);

    public record TimePayload(string? deviceid, TimeData data);

    public static class TailscaleHelpers
    {
        public static string BuildDeviceBaseAddress(string ip, int port)
        {
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("ip must be provided", nameof(ip));

            if (ip.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(ip);
                var builder = new UriBuilder(uri) { Port = port };
                var s = builder.ToString();
                return s.EndsWith("/") ? s : s + "/";
            }

            return $"http://{ip}:{port}/";
        }

        public static TimePayload CreateTimePayload(DateTime nowUtc, string timeZoneId, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
                throw new ArgumentException("timeZoneId must be provided", nameof(timeZoneId));

            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var offset = tz.GetUtcOffset(nowUtc).TotalHours;
            // Format date with milliseconds precision to match device expectation: 2026-02-27T23:34:58.000Z
            var dateStr = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            return new TimePayload(deviceId, new TimeData(offset, dateStr));
        }
    }
}
