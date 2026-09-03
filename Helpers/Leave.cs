using System.Text.Json;

namespace NoisLogTray;

// Pure parser for the HRM find_my_requests(kind: "leave") result. Kept SDK-free so
// it unit-tests without the MCP client, like Timesheet.
internal static class Leave
{
    private const string ApprovedStatus = "Approved";
    private const string AllDayPeriod = "AllDay";

    // A half-day request is still half a working day and needs a real ticket, so only
    // AllDay counts. Anything unexpected yields an empty list rather than an error.
    internal static IReadOnlyList<DateOnly> ParseOffDates(bool isError, string? text)
    {
        if (isError || string.IsNullOrEmpty(text)) return Array.Empty<DateOnly>();

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!TryGetSuccessData(doc.RootElement, out var inner)) return Array.Empty<DateOnly>();
            if (!TryGetSuccessData(inner, out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<DateOnly>();

            var dates = new SortedSet<DateOnly>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!Text(item, "status").Equals(ApprovedStatus, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Text(item, "periodType").Equals(AllDayPeriod, StringComparison.OrdinalIgnoreCase)) continue;

                if (!TryDate(item, "fromDate", out var from)) continue;
                if (!TryDate(item, "toDate", out var to)) to = from;
                if (to < from) continue;

                for (var d = from; d <= to; d = d.AddDays(1)) dates.Add(d);
            }
            return dates.ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<DateOnly>();
        }
    }

    // data.pagination.totalPages, so the caller knows whether to ask for another page.
    // Defaults to 1 (stop) on anything unrecognised.
    internal static int ParseTotalPages(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!TryGetSuccessData(doc.RootElement, out var inner)) return 1;
            if (inner.ValueKind != JsonValueKind.Object) return 1;
            if (!inner.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object)
                return 1;
            if (!pagination.TryGetProperty("totalPages", out var total) || total.ValueKind != JsonValueKind.Number)
                return 1;
            return Math.Max(1, total.GetInt32());
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    // Read an object's "data" only when its sibling "success" is true, so a
    // FORBIDDEN envelope never reaches the item loop.
    private static bool TryGetSuccessData(JsonElement element, out JsonElement data)
    {
        data = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return false;
        return element.TryGetProperty("data", out data);
    }

    private static string Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // The values are ISO datetimes ("2026-08-13T00:00:00"); only the date part matters.
    private static bool TryDate(JsonElement obj, string name, out DateOnly date)
    {
        date = default;
        var raw = Text(obj, name);
        if (raw.Length == 0) return false;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return false;
        date = DateOnly.FromDateTime(parsed);
        return true;
    }
}
