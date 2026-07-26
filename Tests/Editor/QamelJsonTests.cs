using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class QamelJsonTests
    {
        [Test]
        public void WritesFlatObject()
        {
            string json = new QamelJson().Begin()
                .Str("a", "hello")
                .Int("b", 42)
                .Num("c", 1.5)
                .End();
            Assert.AreEqual("{\"a\":\"hello\",\"b\":42,\"c\":1.5}", json);
        }

        [Test]
        public void EscapesSpecialCharacters()
        {
            string json = new QamelJson().Begin()
                .Str("m", "quote:\" slash:\\ nl:\n tab:\t ctrl:\u0001")
                .End();
            Assert.AreEqual("{\"m\":\"quote:\\\" slash:\\\\ nl:\\n tab:\\t ctrl:\\u0001\"}", json);
        }

        [Test]
        public void NullStringBecomesJsonNull()
        {
            Assert.AreEqual("{\"a\":null}", new QamelJson().Begin().Str("a", null).End());
        }

        [Test]
        public void NumbersUseInvariantCultureAndLimitedPrecision()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal culture must not leak into the wire format.
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string json = new QamelJson().Begin().Num("t", 12.345678).End();
                Assert.AreEqual("{\"t\":12.3457}", json);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void BeginResetsTheWriter()
        {
            var writer = new QamelJson();
            writer.Begin().Str("a", "1").End();
            Assert.AreEqual("{\"b\":\"2\"}", writer.Begin().Str("b", "2").End());
        }

        [Test]
        public void OutputParsesAsValidJson()
        {
            string json = new QamelJson().Begin()
                .Str("message", "line1\nline2 \"x\"")
                .Num("t", 3.25)
                .Int("count", 7)
                .End();
            // MiniParse throws on malformed JSON.
            var parsed = TestJson.Parse(json);
            Assert.AreEqual("line1\nline2 \"x\"", parsed["message"]);
            Assert.AreEqual("3.25", parsed["t"]);
            Assert.AreEqual("7", parsed["count"]);
        }

        [Test]
        public void ExtractStringReadsIngestResponse()
        {
            // Shape returned by POST /api/ingest/report (the Qamel capture wire format).
            string json = "{\"reportId\":\"keyed/ab12/game/sess/report_x.zip\"," +
                          "\"bundleUploadUrl\":\"https://upload.example/sign/a%20b?token=demo\"}";
            Assert.AreEqual("https://upload.example/sign/a%20b?token=demo",
                QamelJson.ExtractString(json, "bundleUploadUrl"));
            Assert.AreEqual("keyed/ab12/game/sess/report_x.zip", QamelJson.ExtractString(json, "reportId"));
        }

        [Test]
        public void ExtractStringHandlesEscapesWhitespaceAndMissingFields()
        {
            Assert.AreEqual("a\"b\\c/d", QamelJson.ExtractString("{ \"url\" :  \"a\\\"b\\\\c\\/d\" }", "url"));
            Assert.AreEqual("x", QamelJson.ExtractString("{\"u\":\"\\u0078\"}", "u"));
            Assert.IsNull(QamelJson.ExtractString("{\"other\":\"y\"}", "url"));
            Assert.IsNull(QamelJson.ExtractString("{\"url\":42}", "url"));
            Assert.IsNull(QamelJson.ExtractString("{\"url\":\"unterminated", "url"));
            Assert.IsNull(QamelJson.ExtractString(null, "url"));
            Assert.IsNull(QamelJson.ExtractString("", null));
        }
    }
}
