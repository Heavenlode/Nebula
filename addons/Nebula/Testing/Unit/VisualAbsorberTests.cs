using Godot;
using Nebula.Utility.Nodes;
using Xunit;

namespace Nebula.Testing.Unit;

/// <summary>
/// Tests for NetTransform3D's visual discontinuity absorber -- the blend that hides a change of
/// reference frame (a body's tick-evaluated frame handing off to a predicted world frame) from
/// anything watching the visual node. The pose is authoritative on both sides of such a handover,
/// so the jump is real and cannot be corrected away; it has to be bled off without introducing a
/// second artifact of its own. These pin the weight curve that decides how.
/// </summary>
[NebulaUnitTest]
public class VisualAbsorberTests
{
    private const float Duration = 0.45f;

    /// <summary>Peak |dw/dt| of 1 - smoothstep is 1.5/duration, at the midpoint.</summary>
    private const float PeakSlopePerDuration = 1.5f;

    [NebulaUnitTest]
    public void TestHoldsTheCapturedPoseAtCaptureAndReleasesItAtTheEnd()
    {
        // Full weight on the frame of the capture: the visual does not move at all on the handover,
        // which is the entire point -- a camera reading it that frame sees no step.
        Assert.Equal(1f, NetTransform3D.AbsorbWeight(0f, Duration));

        // And nothing left over at the end: the visual is on its authoritative pose, not parked
        // near it.
        Assert.Equal(0f, NetTransform3D.AbsorbWeight(Duration, Duration));

        // Past the end stays released rather than wrapping or going negative (which would push the
        // visual out the far side of the pose it just converged onto).
        Assert.Equal(0f, NetTransform3D.AbsorbWeight(Duration * 4f, Duration));

        // A nonsensical duration releases immediately instead of dividing by zero.
        Assert.Equal(0f, NetTransform3D.AbsorbWeight(0f, 0f));
        Assert.Equal(0f, NetTransform3D.AbsorbWeight(0f, -1f));
    }

    [NebulaUnitTest]
    public void TestDecaysMonotonically()
    {
        // Never re-grows: a weight that ticked back up would drag the visual backwards against its
        // own motion mid-blend.
        float previous = NetTransform3D.AbsorbWeight(0f, Duration);
        for (int step = 1; step <= 200; step++)
        {
            float weight = NetTransform3D.AbsorbWeight(Duration * step / 200f, Duration);
            Assert.True(weight <= previous, $"weight rose from {previous} to {weight} at step {step}");
            Assert.InRange(weight, 0f, 1f);
            previous = weight;
        }
    }

    [NebulaUnitTest]
    public void TestFlatAtBothEnds()
    {
        // Zero slope at BOTH ends is what makes this a fix rather than a relocation of the problem.
        // A curve that starts steep replaces the position step with a velocity step at the handover;
        // one that ends steep does the same when the blend finishes.
        float atStart = 1f - NetTransform3D.AbsorbWeight(Duration * 0.01f, Duration);
        float atEnd = NetTransform3D.AbsorbWeight(Duration * 0.99f, Duration);

        Assert.True(atStart < 0.001f, $"curve leaves the capture too fast: moved {atStart} in the first 1%");
        Assert.True(atEnd < 0.001f, $"curve arrives too fast: {atEnd} still to give up in the last 1%");
    }

    [NebulaUnitTest]
    public void TestInducedSpeedStaysUnderTheDocumentedBound()
    {
        // The absorber lays a decaying offset over real motion, so the apparent extra speed it adds
        // is |offset| x |dw/dt|. Pin the peak: at the 250-unit cap over the default duration this is
        // the worst drift the technique can produce, and it has to stay well under the speed of the
        // thing carrying it or the blend itself becomes the visible artifact.
        const float dt = 1f / 60f;
        float peak = 0f;

        for (float elapsed = 0f; elapsed < Duration; elapsed += dt)
        {
            float slope = (NetTransform3D.AbsorbWeight(elapsed, Duration)
                           - NetTransform3D.AbsorbWeight(elapsed + dt, Duration)) / dt;
            peak = Mathf.Max(peak, slope);
        }

        float bound = PeakSlopePerDuration / Duration;
        Assert.True(peak <= bound * 1.05f, $"peak slope {peak} exceeded the {bound} bound");
        Assert.True(peak >= bound * 0.9f, $"peak slope {peak} far under {bound} -- curve is not the one documented");
    }

    [NebulaUnitTest]
    public void TestVisualTracksRealMotionThroughTheBlend()
    {
        // The composite the renderer actually sees: authoritative pose + decaying offset. Assert it
        // is continuous at the handover, converges, and -- the property that makes this not just
        // added lag -- keeps moving with the real motion the whole way, never stalling or reversing.
        var velocity = new Vector3(120f, 0f, 0f);   // planet-scale orbital speed
        var offset = new Vector3(-18f, 4f, 0f);     // a plausible frame-change jump
        const float dt = 1f / 60f;

        var authoritativeAtCapture = Vector3.Zero;
        var visualBefore = authoritativeAtCapture + offset;   // where the outgoing frame had it
        var previousVisual = visualBefore;

        for (int frame = 0; frame * dt <= Duration; frame++)
        {
            float elapsed = frame * dt;
            var authoritative = authoritativeAtCapture + velocity * elapsed;
            var visual = authoritative + offset * NetTransform3D.AbsorbWeight(elapsed, Duration);

            if (frame == 0)
            {
                // No step on the handover frame.
                Assert.True(visual.IsEqualApprox(visualBefore), $"handover moved the visual to {visual} from {visualBefore}");
            }
            else
            {
                // Always advancing along the real motion: the offset bleeds off slower than the
                // ship travels, so the visual never goes backwards under the player.
                Assert.True(velocity.Dot(visual - previousVisual) > 0f,
                    $"visual moved against the motion at frame {frame}");
            }

            previousVisual = visual;
        }

        // Landed exactly on the authoritative pose, with nothing carried past the end.
        var authoritativeAtEnd = authoritativeAtCapture + velocity * Duration;
        var visualAtEnd = authoritativeAtEnd + offset * NetTransform3D.AbsorbWeight(Duration, Duration);
        Assert.True(visualAtEnd.IsEqualApprox(authoritativeAtEnd),
            $"ended at {visualAtEnd}, expected {authoritativeAtEnd}");
    }
}
