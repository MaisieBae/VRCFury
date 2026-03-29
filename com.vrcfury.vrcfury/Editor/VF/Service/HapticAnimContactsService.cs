// FORK NOTE (vs upstream VRCFury/VRCFury):
// This file has been significantly extended to support Exit Animations for Depth Actions.
// The upstream version uses a single smoother for all depth animations.
// This fork conditionally builds a dual-smoother architecture when depthAction.useExitSmoothing is true.
// See FORK_CHANGES.md for a full explanation.

using System.Collections.Generic;
using VF.Builder;
using VF.Builder.Haptics;
using VF.Component;
using VF.Injector;
using VF.Utils;
using VF.Utils.Controller; // FORK: Added to support VFAFloat in exit layer transition conditions.

namespace VF.Service
{
    [VFService]
    internal class HapticAnimContactsService
    {
        [VFAutowired] private readonly SmoothingService smoothing;
        [VFAutowired] private readonly ActionClipService actionClipService;
        [VFAutowired] private readonly ControllersService controllers;
        private ControllerManager fx => controllers.GetFx();
        [VFAutowired] private readonly ClipFactoryService clipFactory;
        [VFAutowired] private readonly DbtLayerService dbtLayerService;

        public void CreateAnims(
            string layerName,
            ICollection<VRCFuryHapticSocket.DepthActionNew> actions,
            VFGameObject spsComponentOwner,
            string name,
            SpsDepthContacts contacts
        )
        {
            var actionNum = 0;
            foreach (var depthAction in actions)
            {
                actionNum++;
                var prefix = $"{name}/Anim/{actionNum}";

                var unsmoothed = depthAction.units == VRCFuryHapticSocket.DepthActionUnits.Plugs
                    ? (depthAction.enableSelf ? contacts.closestDistancePlugLengths.Value : contacts.others.distancePlugLengths.Value)
                    : depthAction.units == VRCFuryHapticSocket.DepthActionUnits.Meters
                    ? (depthAction.enableSelf ? contacts.closestDistanceMeters.Value : contacts.others.distanceMeters.Value)
                    : (depthAction.enableSelf ? contacts.closestDistanceLocal.Value : contacts.others.distanceLocal.Value);

                var dbt = dbtLayerService.Create($"{layerName} - {actionNum} - Action");
                var math = dbtLayerService.GetMath(dbt);

                // Map raw contact distance into a normalized 0..1 value based on the configured range.
                var mapped = math.Map(
                    $"{prefix}/Mapped",
                    unsmoothed,
                    depthAction.range.Max(), depthAction.range.Min(),
                    0, 1
                );

                VFAFloat smoothed;

                // FORK: Dual-smoother path. Only entered when the user has enabled exit smoothing.
                // Upstream always takes the else branch (single smoother).
                if (depthAction.useExitSmoothing && depthAction.exitSmoothingSeconds > 0)
                {
                    // Fast smoother: tracks entry depth quickly (uses the normal smoothingSeconds).
                    var smoothedFast = smoothing.Smooth(
                        dbt,
                        $"{prefix}/SmoothedFast",
                        mapped,
                        depthAction.smoothingSeconds
                    );
                    // Slow smoother: decays slowly after the plug exits (uses exitSmoothingSeconds).
                    // This is what makes the socket "close slowly" after withdrawal.
                    var smoothedSlow = smoothing.Smooth(
                        dbt,
                        $"{prefix}/SmoothedSlow",
                        mapped,
                        depthAction.exitSmoothingSeconds
                    );
                    // Main depth value for the primary animation: always the higher of the two smoothers.
                    // Entering: fast rises quickly, slow rises slowly -> Max = fast (responsive entry).
                    // Exiting: fast drops quickly, slow drops slowly -> Max = slow (gradual close).
                    smoothed = math.Max(
                        smoothedFast,
                        smoothedSlow,
                        $"{prefix}/Smoothed"
                    );

                    // FORK: Exit animation layer. Only generated when useExitAnimation is also true.
                    if (depthAction.useExitAnimation)
                    {
                        // Gap signal: smoothedSlow - smoothedFast.
                        // = 0 while plug is entering or stationary (fast >= slow).
                        // > 0 while plug is withdrawing (fast drops below slow).
                        // Holds roughly constant if plug pauses mid-withdrawal (smoothers converge).
                        // Returns to 0 once plug is fully out (both smoothers decay to 0 together).
                        var rawExitDriver = math.Subtract(
                            smoothedSlow,
                            smoothedFast,
                            $"{prefix}/RawExitDriver"
                        );
                        // Remap the gap into 0..1 for use as a transition condition threshold.
                        var exitDriver = math.Map(
                            $"{prefix}/ExitDriver",
                            rawExitDriver,
                            0f, 1f,
                            0f, 1f
                        );

                        // Positional exit value: inverted fast smoother depth.
                        // 0 = plug fully inside socket.
                        // 1 = plug at socket entrance.
                        // This drives the exit clip's playback position (via MotionTime or BlendTree).
                        var exitAnimValue = math.Map(
                            $"{prefix}/ExitAnimValue",
                            smoothedFast,
                            1f, 0f,
                            0f, 1f
                        );

                        var exitLayer = fx.NewLayer($"{layerName} - {actionNum} - Exit");
                        var exitOff = exitLayer.NewState("Off");
                        var exitOn = exitLayer.NewState("On");

                        var exitAction = actionClipService.LoadStateAdv(
                            $"{prefix}/Exit",
                            depthAction.exitActionSet,
                            spsComponentOwner,
                            ActionClipService.MotionTimeMode.Auto
                        );

                        if (exitAction.useMotionTime)
                        {
                            // Exit clip uses motion time: scrub position directly via exitAnimValue.
                            exitOn.WithAnimation(exitAction.onClip).MotionTime(exitAnimValue);
                        }
                        else
                        {
                            // Exit clip is a static action: use a blend tree to cross-fade
                            // between empty and the clip based on exitAnimValue.
                            var exitTree = VFBlendTree1D.Create($"{prefix}/Exit tree", exitAnimValue);
                            exitTree.Add(0, clipFactory.GetEmptyClip());
                            exitTree.Add(1, exitAction.onClip);
                            exitOn.WithAnimation(exitTree);
                        }

                        // Enter exitOn when plug is actively withdrawing AND is still at least
                        // partially inside the socket. The smoothedFast > 0.01 guard prevents
                        // re-triggering from the slow smoother's lingering decay after full exit.
                        var exitWhen = exitDriver.IsGreaterThan(0.01f).And(smoothedFast.IsGreaterThan(0.01f));
                        exitOff.TransitionsTo(exitOn).When(exitWhen);

                        // Leave exitOn when plug is fully gone. Fades out over exitAnimFadeSeconds
                        // for a smooth blend back to zero rather than an abrupt cut.
                        exitOn.TransitionsTo(exitOff)
                            .When(smoothedFast.IsLessThan(0.005f))
                            .WithTransitionDurationSeconds(depthAction.exitAnimFadeSeconds);
                    }
                }
                else
                {
                    // UPSTREAM path: single smoother, identical to original VRCFury behavior.
                    smoothed = smoothing.Smooth(
                        dbt,
                        $"{prefix}/Smoothed",
                        mapped,
                        depthAction.smoothingSeconds
                    );
                }

                var layer = fx.NewLayer($"{layerName} - {actionNum} - Action");
                var off = layer.NewState("Off");
                var on = layer.NewState("On");

                var action = actionClipService.LoadStateAdv(prefix, depthAction.actionSet, spsComponentOwner, ActionClipService.MotionTimeMode.Auto);
                if (action.useMotionTime)
                {
                    on.WithAnimation(action.onClip).MotionTime(smoothed);
                    if (depthAction.reverseClip)
                    {
                        foreach (var clip in new AnimatorIterator.Clips().From(action.onClip))
                        {
                            clip.Reverse();
                        }
                    }
                }
                else
                {
                    var tree = VFBlendTree1D.Create(prefix + " tree", smoothed);
                    tree.Add(0, clipFactory.GetEmptyClip());
                    tree.Add(1, action.onClip);
                    on.WithAnimation(tree);
                }

                if (depthAction.reverseClip)
                {
                    off.TransitionsTo(on).When(fx.Always());
                }
                else
                {
                    var onWhen = smoothed.IsGreaterThan(0.01f);
                    off.TransitionsTo(on).When(onWhen);
                    on.TransitionsTo(off).When(onWhen.Not());
                }
            }
        }
    }
}