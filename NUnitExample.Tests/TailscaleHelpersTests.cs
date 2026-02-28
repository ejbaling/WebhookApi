using System;
using System.Text.Json;
using NUnit.Framework;
using WebhookApi.Services;

namespace NUnitExample.Tests
{
    [TestFixture]
    public class TailscaleHelpersTests
    {
        [Test]
        public void BuildDeviceBaseAddress_WithHttpUri_AppliesPort()
        {
            var res = TailscaleHelpers.BuildDeviceBaseAddress("http://example.com", 8081);
            Assert.That(res, Does.StartWith("http://example.com:8081/"));
        }

        [Test]
        public void BuildDeviceBaseAddress_WithoutScheme_BuildsHttpUri()
        {
            var res = TailscaleHelpers.BuildDeviceBaseAddress("192.168.1.5", 8081);
            Assert.That(res, Is.EqualTo("http://192.168.1.5:8081/"));
        }

        [Test]
        public void CreateTimePayload_ReturnsExpectedOffsetAndDate()
        {
            var now = new DateTime(2024, 01, 02, 03, 04, 05, DateTimeKind.Utc);
            var payload = TailscaleHelpers.CreateTimePayload(now, "UTC", "dev123");
            Assert.That(payload.deviceid, Is.EqualTo("dev123"));
            Assert.That(payload.data.timeZone, Is.EqualTo(0));
            Assert.That(payload.data.date, Is.EqualTo(now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")));
        }

        [Test]
        public void CreateTimePayload_AsiaManila_ReturnsOffsetPlus8()
        {
            var now = new DateTime(2024, 06, 15, 12, 0, 0, DateTimeKind.Utc);
            var payload = TailscaleHelpers.CreateTimePayload(now, "Asia/Manila", null);
            Assert.That(payload.data.timeZone, Is.EqualTo(8));
        }

        [Test]
        public void CreateTimePayload_SerializesToExpectedJson()
        {
            var now = new DateTime(2024, 06, 15, 12, 0, 0, DateTimeKind.Utc);
            var payload = TailscaleHelpers.CreateTimePayload(now, "Asia/Manila", "dev123");

            var opts = new JsonSerializerOptions { WriteIndented = false };
            var json = JsonSerializer.Serialize(payload, opts);

            var expected = "{\"deviceid\":\"dev123\",\"data\":{\"timeZone\":8,\"date\":\"2024-06-15T12:00:00.000Z\"}}";
            Assert.That(json, Is.EqualTo(expected));
        }
    }
}
