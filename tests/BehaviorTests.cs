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
        var settings = new Settings
        {
            ProcessName = "OtherGame", CenterX = 1, CenterY = 2, Radius = 3,
            ArrowThreshold = .1, RingThreshold = .2, IntervalMs = 500,
            Cover = false, ShowDelayMs = 999, HideDelayMs = 888, CloseToTray = false, DrivingOnly = false, ShowWhenAppFocused = false
        };
        settings.RestoreDefaults();
        Check("restore_resets_every_setting", JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(new Settings()));
        Check("default_cover_and_tray_enabled", settings.Cover && settings.CloseToTray);
        Check("default_detection_interval_33ms", settings.IntervalMs == 33);
        Check("default_visibility_delays_100ms", settings.ShowDelayMs == 100 && settings.HideDelayMs == 100);
        Check("driving_only_enabled_by_default", settings.DrivingOnly);
        foreach (string keyword in DrivingDetector.Keywords)
            Check($"driving_keyword_{keyword}", DrivingDetector.MatchKeywords([string.Join(" ", keyword.ToCharArray())]).Contains(keyword));
        Check("brake_reverse_contains_brake", DrivingDetector.MatchKeywords(["LT 刹 车 / 倒 车"]).Contains("刹车"));
        Check("photo_hints_are_not_driving", DrivingDetector.MatchKeywords(["拍照", "操控镜头", "拉近/拉远镜头", "智能驾驶"]).Length == 0);
        Check("walking_hints_are_not_driving", DrivingDetector.MatchKeywords(["闪避", "跳跃", "攻击", "上车"]).Length == 0);
        Check("keywords_cannot_span_separate_lines", DrivingDetector.MatchKeywords(["下", "车"]).Length == 0);
        var search = DrivingDetector.Region(new System.Drawing.Rectangle(100, 50, 1920, 1080));
        Check("driving_search_covers_broad_right_area", search.Width == 672 && search.Height == 810 && search.Right == 2020 && search.Bottom == 1130);
        File.WriteAllText("behavior-test-results.json", JsonSerializer.Serialize(new { failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
