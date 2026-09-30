using System;

namespace QamelCapture.TestAuthoring
{
    /// <summary>
    /// Optional game-owned boundary for observing the outcome of a short Test.
    /// The value must be canonical and stable enough for exact comparison. The
    /// summary is retained only to make a mismatch understandable to a person.
    /// </summary>
    public interface IQamelTestOutcomeProvider
    {
        int OutcomeFormatVersion { get; }
        TestOutcomeObservation ObserveOutcome();
    }

    public sealed class TestOutcomeObservation
    {
        public TestOutcomeObservation(string value, string summary)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("An outcome value is required.", nameof(value));
            if (value.Length > 4096)
                throw new ArgumentException(
                    "An outcome value cannot exceed 4096 characters.",
                    nameof(value));
            if (summary != null && summary.Length > 1000)
                throw new ArgumentException(
                    "An outcome summary cannot exceed 1000 characters.",
                    nameof(summary));

            Value = value;
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        }

        public string Value { get; }
        public string Summary { get; }
    }

    internal sealed class TestExpectedOutcomeObservation
    {
        public TestExpectedOutcomeObservation(
            string providerId,
            int formatVersion,
            TestOutcomeObservation observation)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("An outcome provider ID is required.", nameof(providerId));
            if (formatVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(formatVersion));
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            ProviderId = providerId;
            FormatVersion = formatVersion;
        }

        public string ProviderId { get; }
        public int FormatVersion { get; }
        public TestOutcomeObservation Observation { get; }
    }

    internal sealed class TestOutcomeComparison
    {
        public TestOutcomeComparison(
            TestOutcomeObservation expected,
            TestOutcomeObservation actual)
        {
            Expected = expected ?? throw new ArgumentNullException(nameof(expected));
            Actual = actual ?? throw new ArgumentNullException(nameof(actual));
        }

        public TestOutcomeObservation Expected { get; }
        public TestOutcomeObservation Actual { get; }
        public bool Matches => string.Equals(
            Expected.Value,
            Actual.Value,
            StringComparison.Ordinal);
    }
}
