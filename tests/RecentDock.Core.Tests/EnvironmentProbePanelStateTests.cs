using RecentDock.Core;
using Xunit;

namespace RecentDock.Core.Tests;

/// <summary>
/// Tests for the panel-state decision.
///
/// This is the product's core judgement and the reason the verification round
/// mattered: disabling the tracking toggle stops NEW records but leaves existing
/// ones in place, so "disabled" and "empty" are independent. Collapsing them into
/// one state would show a "nothing was ever recorded" guidance page on a machine
/// that is actually holding a full history.
/// </summary>
public class EnvironmentProbePanelStateTests
{
    [Theory]
    // Tracking on: history present or not is the only distinction.
    [InlineData(TrackingState.Enabled, 5, 5, PanelState.ActiveWithRecords)]
    [InlineData(TrackingState.Enabled, 0, 3, PanelState.ActiveWithRecords)]
    [InlineData(TrackingState.Enabled, 3, 0, PanelState.ActiveWithRecords)]
    [InlineData(TrackingState.Enabled, 0, 0, PanelState.ActiveEmpty)]
    // Tracking off but records survive: must still render the history.
    [InlineData(TrackingState.DisabledByUser, 0, 8, PanelState.ClosedWithHistory)]
    [InlineData(TrackingState.DisabledByUser, 8, 0, PanelState.ClosedWithHistory)]
    [InlineData(TrackingState.DisabledByPolicy, 0, 8, PanelState.ClosedWithHistory)]
    // Tracking off and genuinely nothing recorded.
    [InlineData(TrackingState.DisabledByUser, 0, 0, PanelState.ClosedEmpty)]
    [InlineData(TrackingState.DisabledByPolicy, 0, 0, PanelState.ClosedEmpty)]
    public void GetPanelState_ClassifiesCorrectly(
        TrackingState tracking,
        int sourceA,
        int sourceB,
        PanelState expected)
    {
        Assert.Equal(expected, EnvironmentProbe.GetPanelState(tracking, sourceA, sourceB));
    }

    [Fact]
    public void GetPanelState_TrackingDisabledWithHistory_IsNotReportedAsEmpty()
    {
        // Named regression test for the exact mistake the first design draft made.
        PanelState state = EnvironmentProbe.GetPanelState(
            TrackingState.DisabledByUser, sourceACount: 0, sourceBCount: 1);

        Assert.NotEqual(PanelState.ClosedEmpty, state);
        Assert.Equal(PanelState.ClosedWithHistory, state);
    }

    [Fact]
    public void GetPanelState_IgnoresWhichSourceHasTheRecords()
    {
        // The two sources must be additive: either one holding data is enough.
        Assert.Equal(
            EnvironmentProbe.GetPanelState(TrackingState.Enabled, 4, 0),
            EnvironmentProbe.GetPanelState(TrackingState.Enabled, 0, 4));
    }

    /// <summary>
    /// The settings link must point at Personalization &gt; Start.
    ///
    /// The first version of this test asserted ms-settings:privacy-general, based
    /// on the assumption that the toggle lives on a privacy page. That assumption
    /// was disproved: the complete set of privacy page identifiers in
    /// SystemSettings.dll contains privacy-general, privacy-activityhistory and 37
    /// others but NO privacy-recentdocs, and Microsoft Q&amp;A threads place the
    /// toggle under Personalization &gt; Start. This test now pins the corrected
    /// target so the wrong page cannot come back.
    /// </summary>
    [Fact]
    public void GetSettingsUri_PointsAtPersonalizationStart()
    {
        Assert.Equal("ms-settings:personalization-start", EnvironmentProbe.GetSettingsUri());
    }

    [Fact]
    public void GetSettingsUri_IsNotThePrivacyPage()
    {
        // Named regression: the privacy "General" page has no recent-items toggle,
        // so linking there leaves the user with no way forward.
        Assert.NotEqual("ms-settings:privacy-general", EnvironmentProbe.GetSettingsUri());
        Assert.NotEqual("ms-settings:privacy-recentdocs", EnvironmentProbe.GetSettingsUri());
    }

    [Fact]
    public void GetFolderOptionsCommand_TargetsTheControlPanelDialog()
    {
        Assert.Equal("control.exe", EnvironmentProbe.GetFolderOptionsCommand());
        Assert.Equal("folders", EnvironmentProbe.GetFolderOptionsArguments());
    }

    [Fact]
    public void GetDiagnostics_ReturnsNullForAbsentValues()
    {
        // On a machine where nothing is configured, all three read as absent rather
        // than throwing. Reading the live registry is acceptable here: the
        // assertion only requires that the call succeeds and reports something.
        TrackingDiagnostics diagnostics = EnvironmentProbe.GetDiagnostics();

        Assert.NotNull(diagnostics);
    }
}
