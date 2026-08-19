namespace Autobahn.Internal;

/// <summary>Something wrong with where or how reports were asked to be written.</summary>
internal abstract record ReportError : AppError
{
    public sealed record EmptyReportName : ReportError
    {
        public override string Message => "Report file name cannot be empty string";
    }

    public sealed record InvalidReportName : ReportError
    {
        public override string Message =>
            $"Report file name contains invalid chars: '{new string(Path.GetInvalidFileNameChars())}'";
    }

    public sealed record EmptyReportFolderPath : ReportError
    {
        public override string Message => "Report folder path cannot be empty string";
    }

    public sealed record InvalidReportFolderPath : ReportError
    {
        public override string Message =>
            $"Report folder path contains invalid chars: '{new string(Path.GetInvalidPathChars())}'";
    }

    public sealed record ReportingIntervalSmallerThanMin : ReportError
    {
        public override string Message =>
            $"ReportingInterval should be bigger than min value: '{(int)Constants.MinReportingInterval.TotalSeconds}'";
    }
}
