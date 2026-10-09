namespace NoisLogTray;

internal static class PrUpdateChecker
{
    // UI thread only (no ConfigureAwait(false)): it reads and writes TicketStore.
    // baselineOnly: record every PR's current state without reporting anything.
    internal static async Task<PrCheckResult> CheckAsync(IAzureDevOpsClient ado, TicketStore store, bool baselineOnly)
    {
        var tracking = store.LoadPrTracking();
        var trackingBefore = new Dictionary<string, TrackedPr>(tracking);
        var isLegacyRemoved = PrTracker.RemoveLegacyKeys(tracking);

        var started = store.LoadStarted();
        var done = store.LoadDone();
        var trackedTicketKeys = started.Keys
            .Concat(done.Keys.Where(key => !PrTracker.AreAllPrsClosed(tracking, key)))
            .Distinct()
            .ToList();

        var notifications = new List<(string TicketKey, PrNotification Notification)>();
        var devMergedNeedingReadyToTest = new List<string>();
        var isComplete = true;

        foreach (var ticketKey in trackedTicketKeys)
        {
            PrLookupResult lookup;
            try
            {
                lookup = await ado.GetTicketPrsAsync(ticketKey);
            }
            // Network down: every remaining ticket would fail the same way, so retry on the next tick.
            catch (Exception ex) when (AzureDevOpsClient.IsNetworkFailure(ex))
            {
                AppLogger.Error($"[ado] {ticketKey}: {ex.Message} -- skipped remaining PR checks");
                isComplete = false;
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[ado] {ticketKey}: {ex.Message}");
                isComplete = false;
                continue;
            }

            if (!lookup.IsComplete)
            {
                isComplete = false;
            }

            var ticketStatus = (started.GetValueOrDefault(ticketKey) ?? done.GetValueOrDefault(ticketKey))?.Status;
            foreach (var notification in PrTracker.DetectChanges(tracking, ticketKey, lookup.Prs))
            {
                if (baselineOnly)
                {
                    continue;
                }

                notifications.Add((ticketKey, notification));

                // Deployed to Dev / Ready to Test stay manual (CI deploys after the merge), so this only reminds.
                // Only once every dev PR (BE and FE) is in: a merged BE PR with the FE one still open isn't ready.
                if (notification.IsMerged
                    && notification.Pr.Branch == "dev"
                    && PrTracker.IsDevPrMerged(tracking, ticketKey)
                    && JiraStatusFlow.GetStepsToReadyToTest(ticketStatus).Count != 0)
                {
                    devMergedNeedingReadyToTest.Add(ticketKey);
                }
            }
        }

        var isTrackingChanged = isLegacyRemoved
            || tracking.Count != trackingBefore.Count
            || tracking.Any(entry => !trackingBefore.TryGetValue(entry.Key, out var before) || before != entry.Value);
        if (isTrackingChanged)
        {
            store.SavePrTracking(tracking);
        }

        return new PrCheckResult(notifications, devMergedNeedingReadyToTest.Distinct().ToList(), isComplete);
    }
}
