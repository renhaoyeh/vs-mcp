using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using VsMcp.Contracts;

namespace VsMcp.Services.Testing
{
    internal static class TrxReportReader
    {
        public static TestReport Read(string path, int maxResults)
        {
            if (maxResults < 1 || maxResults > 1000) throw new ArgumentOutOfRangeException(nameof(maxResults));
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
            {
                var root = XDocument.Load(reader);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                var counters = root.Root?.Element(ns + "ResultSummary")?.Element(ns + "Counters") ?? throw new InvalidDataException("TRX contains no result counters.");
                int Count(string name) => int.TryParse((string)counters.Attribute(name), out var n) && n >= 0 ? n : throw new InvalidDataException("Invalid TRX counter: " + name);
                var report = new TestReport { Total = Count("total"), Executed = Count("executed"), Passed = Count("passed"), Failed = Count("failed") };
                report.Skipped = Math.Max(0, report.Total - report.Executed);
                var rows = root.Root?.Element(ns + "Results")?.Elements(ns + "UnitTestResult").ToArray() ?? new XElement[0];
                report.Truncated = rows.Length > maxResults;
                report.Results = rows.OrderByDescending(e => (string)e.Attribute("outcome") == "Failed").Take(maxResults).Select(e => new TestCaseResult
                {
                    Name = (string)e.Attribute("testName"), Outcome = (string)e.Attribute("outcome"), Duration = (string)e.Attribute("duration"),
                    ErrorMessage = Limit((string)e.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "Message")),
                    StackTrace = Limit((string)e.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace")),
                }).ToList();
                return report;
            }
        }
        private static string Limit(string value) => value != null && value.Length > 16000 ? value.Substring(0, 16000) + "\n[truncated]" : value;
    }
}
