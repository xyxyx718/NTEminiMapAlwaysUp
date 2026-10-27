using System.Text.Json;

namespace MinimapUp;

static class BehaviorTests
{
    public static void Run()
    {
        var results = new List<object>();
        int failures = 0;
        void Check(string name, bool condition)
        {
            results.Add(new { name, passed = condition });
            if (!condition) failures++;
        }
        var filter = new VisibilityFilter();
        Check("initially_hidden", !filter.Visible);
        Check("show_wait_starts_at_first_hit", !filter.Update(true, 1000, 100, 200));
        Check("short_hit_does_not_show", !filter.Update(true, 1099, 100, 200));
        Check("miss_cancels_pending_show", !filter.Update(false, 1100, 100, 200));
        Check("new_hit_restarts_show_wait", !filter.Update(true, 2000, 100, 200));
        Check("show_at_deadline", filter.Update(true, 2100, 100, 200));
        Check("short_miss_keeps_map_visible", filter.Update(false, 2200, 100, 200));
        Check("hide_not_before_deadline", filter.Update(false, 2399, 100, 200));
        Check("recovery_cancels_pending_hide", filter.Update(true, 2400, 100, 200));
        Check("new_miss_restarts_hide_wait", filter.Update(false, 2500, 100, 200));
        Check("hide_at_deadline", !filter.Update(false, 2700, 100, 200));
        Check("zero_show_delay", filter.Update(true, 2800, 0, 200));
        Check("zero_hide_delay", !filter.Update(false, 2801, 0, 0));
        filter.Update(true, 3000, 0, 200);
        filter.Reset();
        Check("reset_hides_immediately", !filter.Visible);
        Check("reset_discards_previous_tracking", !filter.Update(true, 4000, 100, 200));
        // A long capture stall must not require an arbitrary number of frames.
        Check("elapsed_time_independent_of_frame_rate", filter.Update(true, 4800, 100, 200));
        File.WriteAllText("behavior-test-results.json", JsonSerializer.Serialize(new { failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
