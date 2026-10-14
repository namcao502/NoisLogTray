namespace NoisLogTray.Tests;

public class SettingsPanelTests
{
    private static readonly string LongToken = new('x', 200);

    [Fact]
    public void SavedSecret_ShowsAShortMask_AndKeepsItsValue()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string> { ["JIRA_API_TOKEN"] = LongToken });

        var passwordBoxes = AllTextBoxes(panel).Where(tb => tb.UseSystemPasswordChar).ToList();

        Assert.Contains(passwordBoxes, tb => tb.Text.Length == 8);
        Assert.DoesNotContain(passwordBoxes, tb => tb.Text.Length == LongToken.Length);
        Assert.Equal(LongToken, panel.CurrentValues()["JIRA_API_TOKEN"]);
    }

    [Fact]
    public void EmptySecret_ShowsNoMask()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string>());

        Assert.Equal("", panel.CurrentValues()["HRM_API_KEY"]);
        Assert.All(AllTextBoxes(panel).Where(tb => tb.UseSystemPasswordChar), tb => Assert.Equal("", tb.Text));
    }

    [Fact]
    public void TypedSecret_ReplacesTheSavedValue()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string> { ["JIRA_API_TOKEN"] = LongToken });
        var maskedBox = AllTextBoxes(panel).Single(tb => tb.UseSystemPasswordChar && tb.Text.Length == 8);

        maskedBox.Text = "new-token";

        Assert.Equal("new-token", panel.CurrentValues()["JIRA_API_TOKEN"]);
    }

    [Fact]
    public void HalfDeletedMask_IsNeverSavedAsTheSecret()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string> { ["JIRA_API_TOKEN"] = LongToken });
        var maskedBox = AllTextBoxes(panel).Single(tb => tb.UseSystemPasswordChar && tb.Text.Length == 8);

        maskedBox.Text = maskedBox.Text[..7]; // one Backspace at the end of the mask

        Assert.Equal("", panel.CurrentValues()["JIRA_API_TOKEN"]);
    }

    [Fact]
    public void TypingAfterTheMask_KeepsOnlyWhatWasTyped()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string> { ["JIRA_API_TOKEN"] = LongToken });
        var maskedBox = AllTextBoxes(panel).Single(tb => tb.UseSystemPasswordChar && tb.Text.Length == 8);

        maskedBox.Text += "a";

        Assert.Equal("a", panel.CurrentValues()["JIRA_API_TOKEN"]);
    }

    [Fact]
    public void MaskRestoredAfterAnEdit_IsNeverSavedAsTheSecret()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string> { ["JIRA_API_TOKEN"] = LongToken });
        var maskedBox = AllTextBoxes(panel).Single(tb => tb.UseSystemPasswordChar && tb.Text.Length == 8);
        var mask = maskedBox.Text;

        maskedBox.Text = "a";
        maskedBox.Text = mask; // Ctrl+Z back to the mask

        Assert.Equal("", panel.CurrentValues()["JIRA_API_TOKEN"]);
    }

    [Fact]
    public void ThemePills_ShowTheCurrentTheme()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string>());
        var buttons = panel.Controls.OfType<MacButton>().ToList();

        var dark = buttons.Single(mb => mb.Text == Lang.T("Dark", "Tối"));
        var light = buttons.Single(mb => mb.Text == Lang.T("Light", "Sáng"));

        Assert.Equal(Theme.Dark, dark.Selected);
        Assert.Equal(!Theme.Dark, light.Selected);
    }

    [Fact]
    public void TicketCheckPills_OfferEveryChoice_AndSelectTheCurrentInterval()
    {
        using var panel = new SettingsPanel(new Dictionary<string, string>());
        var buttons = panel.Controls.OfType<MacButton>().ToList();

        foreach (var minutes in TicketCheckInterval.ChoicesMinutes)
        {
            var pill = buttons.Single(mb => mb.Text == minutes.ToString());
            Assert.Equal(minutes == TicketCheckInterval.Minutes, pill.Selected);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buttons_StayInsideThePanel_AndNeverOverlap(bool wide)
    {
        var width = wide ? SettingsPanel.WideMinWidth : SettingsPanel.ClientW;
        using var panel = new SettingsPanel(new Dictionary<string, string>(), width, onCleanupWorktrees: () => { }, onRebuild: () => { },
            onSchedules: () => { });
        var buttons = panel.Controls.OfType<MacButton>().ToList();

        Assert.Contains(buttons, mb => mb.Text == Lang.T("Rebuild and restart", "Build lại và khởi động lại"));
        Assert.All(buttons, mb => Assert.True(mb.Right <= panel.Width && mb.Bottom <= panel.Height, mb.Text));
        foreach (var button in buttons)
        {
            Assert.DoesNotContain(buttons, mb => mb != button && mb.Bounds.IntersectsWith(button.Bounds));
        }
    }

    private static IEnumerable<TextBox> AllTextBoxes(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBox textBox)
            {
                yield return textBox;
            }

            foreach (var nested in AllTextBoxes(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void SchedulesButton_IsThere_OnlyWithItsAction_AndCarriesTheDot()
    {
        using var without = new SettingsPanel(new Dictionary<string, string>());
        using var with = new SettingsPanel(new Dictionary<string, string>(), onSchedules: () => { });

        Assert.Null(without.SchedulesButton);
        Assert.NotNull(with.SchedulesButton);
        Assert.False(with.SchedulesButton!.ShowDot);
        with.ShowSchedulesDot(true);
        Assert.True(with.SchedulesButton.ShowDot);
        without.ShowSchedulesDot(true); // no button: nothing to mark, no throw
    }

    [Fact]
    public void SchedulesButton_RunsOnlyItsOwnAction()
    {
        var rebuilds = 0;
        var schedules = 0;
        using var panel = new SettingsPanel(new Dictionary<string, string>(), onCleanupWorktrees: () => { }, onRebuild: () => rebuilds++,
            onSchedules: () => schedules++);

        panel.SchedulesButton!.PerformClick();

        Assert.Equal(1, schedules);
        Assert.Equal(0, rebuilds);
    }
}
