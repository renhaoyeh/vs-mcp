#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VsMcp.Contracts
{
    internal enum TestRunStatus { Running, Passed, Failed, Cancelled, Error, NoTests }

    internal sealed class TestCaseResult
    {
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string Outcome { get; set; } = "";
        public string? Duration { get; set; }
        public string? ErrorMessage { get; set; }
        public string? StackTrace { get; set; }
    }

    internal sealed class TestReport
    {
        [JsonRequired] public int Total { get; set; }
        [JsonRequired] public int Executed { get; set; }
        [JsonRequired] public int Passed { get; set; }
        [JsonRequired] public int Failed { get; set; }
        [JsonRequired] public int Skipped { get; set; }
        [JsonRequired] public List<TestCaseResult> Results { get; set; } = new List<TestCaseResult>();
        [JsonRequired] public bool Truncated { get; set; }
    }

    internal sealed class TestRunResult
    {
        [JsonRequired] public string RunId { get; set; } = "";
        [JsonRequired] public TestRunStatus Status { get; set; }
        [JsonRequired] public string[] Sources { get; set; } = new string[0];
        [JsonRequired] public DateTime StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public int? ExitCode { get; set; }
        public string? ResultsPath { get; set; }
        public string? Error { get; set; }
        public TestReport? Report { get; set; }
        [JsonRequired] public string Output { get; set; } = "";
        [JsonRequired] public bool CancelRequested { get; set; }
    }
}
