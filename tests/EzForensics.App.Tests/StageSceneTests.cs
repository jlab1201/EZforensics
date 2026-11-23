using System.Windows;
using EzForensics.App.Controls;
using EzForensics.Core.Spectre;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The walk-through stage scenes: one per stage, and the loop starts and stops cleanly with animations on or off.</summary>
[Collection("WpfApp")]
public sealed class StageSceneTests
{
    [Fact]
    public void Every_stage_has_its_own_scene_and_unknown_stages_fall_back()
    {
        foreach (var stage in StoryGraphics.StageOrder)
        {
            StageScene.HasScene(stage).Should().BeTrue(stage);
            StageScene.SceneFor(stage).Should().Be(stage);
        }
        StageScene.HasScene("Ingress Tool Transfer").Should().BeTrue();
        StageScene.SceneFor("Command and Control", "T1027, T1105").Should().Be("Ingress Tool Transfer");
        StageScene.SceneFor("Execution", "T1105").Should().Be("Execution");
        StageScene.SceneFor("Something new").Should().Be(PlainLanguage.OtherActivity);
        StageScene.SceneFor(null).Should().Be(PlainLanguage.OtherActivity);
        StageScene.SceneFor("").Should().Be(PlainLanguage.OtherActivity);
    }

    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Scenes_start_and_stop_with_motion_off_and_on()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var off in new[] { true, false })
                {
                    Motion.ForceOff = off;
                    foreach (var stage in StoryGraphics.StageOrder.Append("Unknown stage"))
                    {
                        var scene = new StageScene { Stage = stage, Severity = "High" };
                        scene.Measure(new Size(StageScene.SceneWidth, StageScene.SceneHeight));
                        scene.Arrange(new Rect(0, 0, StageScene.SceneWidth, StageScene.SceneHeight));
                        scene.DesiredSize.Should().Be(new Size(StageScene.SceneWidth, StageScene.SceneHeight));
                        scene.TrackCount.Should().BeGreaterThan(0, stage);
                        scene.TrackCount.Should().BeLessThanOrEqualTo(24, stage);   // a handful of moving parts
                        scene.Run();
                        scene.IsAnimating.Should().Be(Motion.Enabled);
                        if (Motion.Enabled) { Pump(60); scene.AnyAnimated().Should().BeTrue(stage); }
                        // a new stage (next slide) restarts on a fresh scene; the old loop is gone
                        scene.Stage = stage == "Impact" ? "Discovery" : "Impact";
                        scene.IsAnimating.Should().BeFalse();
                        scene.AnyAnimated().Should().BeFalse();
                        scene.Run();
                        scene.IsAnimating.Should().Be(Motion.Enabled);
                        scene.Halt();
                        scene.IsAnimating.Should().BeFalse();
                        scene.AnyAnimated().Should().BeFalse();
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Motion.ForceOff = false; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("Stage scene failed: " + failure, failure);
    }
}
