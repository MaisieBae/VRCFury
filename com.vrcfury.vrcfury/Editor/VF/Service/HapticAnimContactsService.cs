using System.Collections.Generic;
using VF.Builder;
using VF.Builder.Haptics;
using VF.Component;
using VF.Injector;
using VF.Utils;
using VF.Utils.Controller;

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

                var mapped = math.Map(
                    $"{prefix}/Mapped",
                    unsmoothed,
                    depthAction.range.Max(), depthAction.range.Min(),
                    0, 1
                );

                VFAFloat smoothed;

                if (depthAction.useExitSmoothing && depthAction.exitSmoothingSeconds > 0)
                {
                    var smoothedFast = smoothing.Smooth(
                        dbt,
                        $"{prefix}/SmoothedFast",
                        mapped,
                        depthAction.smoothingSeconds
                    );
                    var smoothedSlow = smoothing.Smooth(
                        dbt,
                        $"{prefix}/SmoothedSlow",
                        mapped,
                        depthAction.exitSmoothingSeconds
                    );
                    smoothed = math.Max(
                        smoothedFast,
                        smoothedSlow,
                        $"{prefix}/Smoothed"
                    );

                    if (depthAction.useExitAnimation)
                    {
                        // Gap signal — doorman only.
                        // Opens when smoothedFast drops below smoothedSlow (plug withdrawing).
                        // Used exclusively to trigger entry into exitOn.
                        var rawExitDriver = math.Subtract(
                            smoothedSlow,
                            smoothedFast,
                            $"{prefix}/RawExitDriver"
                        );
                        var exitDriver = math.Map(
                            $"{prefix}/ExitDriver",
                            rawExitDriver,
                            0f, 1f,
                            0f, 1f
                        );

                        // Positional exit value — inverted depth.
                        // 0 = plug fully inside, 1 = plug at socket entrance.
                        // Holds when plug stops, reverses when plug re-enters.
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
                            exitOn.WithAnimation(exitAction.onClip).MotionTime(exitAnimValue);
                        }
                        else
                        {
                            var exitTree = VFBlendTree1D.Create($"{prefix}/Exit tree", exitAnimValue);
                            exitTree.Add(0, clipFactory.GetEmptyClip());
                            exitTree.Add(1, exitAction.onClip);
                            exitOn.WithAnimation(exitTree);
                        }

                        // Enter exitOn only when plug is withdrawing AND still present.
                        // smoothedFast guard blocks re-entry while exitDriver is still
                        // open from the decaying slow smoother after full exit.
                        var exitWhen = exitDriver.IsGreaterThan(0.01f).And(smoothedFast.IsGreaterThan(0.01f));
                        exitOff.TransitionsTo(exitOn).When(exitWhen);

                        // Leave exitOn when plug is fully gone (fast smoother hits zero).
                        // Fades out over exitAnimFadeSeconds for a natural blend-out.
                        exitOn.TransitionsTo(exitOff)
                            .When(smoothedFast.IsLessThan(0.005f))
                            .WithTransitionDurationSeconds(depthAction.exitAnimFadeSeconds);
                    }
                }
                else
                {
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