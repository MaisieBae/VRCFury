using System.Collections.Generic;
using VF.Builder;
using VF.Builder.Haptics;
using VF.Component;
using VF.Injector;
using VF.Utils;
using VF.Utils.Controller;

namespace VF.Service
{
    /**
     * This can build the contacts needed for haptic component depth animations
     */
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
                        // Raw difference: positive only while slow smoother is draining
                        // after the fast smoother has dropped (plug is leaving)
                        var rawExitDriver = math.Subtract(
                            smoothedSlow,
                            smoothedFast,
                            $"{prefix}/RawExitDriver"
                        );

                        // Gate: 0 while plug is still partially inside (smoothedFast > 0),
                        // ramps to 1 only once smoothedFast has returned to zero.
                        // This makes the exit animation hold while any part of the plug
                        // is still registering, mirroring how the depth animation holds during entry.
                        var plugGone = math.Map(
                            $"{prefix}/PlugGone",
                            smoothedFast,
                            0.01f, 0f,
                            0f, 1f
                        );

                        // exitDriver is suppressed to 0 while plug is still inside,
                        // and only begins rising once the plug has fully exited
                        var exitDriver = math.Multiply(
                            $"{prefix}/ExitDriver",
                            rawExitDriver,
                            plugGone
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
                            exitOn.WithAnimation(exitAction.onClip).MotionTime(exitDriver);
                        }
                        else
                        {
                            var exitTree = VFBlendTree1D.Create($"{prefix}/Exit tree", exitDriver);
                            exitTree.Add(0, clipFactory.GetEmptyClip());
                            exitTree.Add(1, exitAction.onClip);
                            exitOn.WithAnimation(exitTree);
                        }

                        var exitWhen = exitDriver.IsGreaterThan(0.01f);
                        exitOff.TransitionsTo(exitOn).When(exitWhen);
                        exitOn.TransitionsTo(exitOff).When(exitWhen.Not());
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
