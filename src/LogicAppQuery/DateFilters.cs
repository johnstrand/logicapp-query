using System.Globalization;

namespace LogicAppQuery;

internal static class DateFilters
{
    internal static DateTimeOffset? NormalizeEndDate(DateTimeOffset? end, string? input)
    {
        if (end.HasValue && DateOnly.TryParseExact(input, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return end.Value.AddTicks(TimeSpan.TicksPerDay - 1000);
        }

        return end;
    }
}
