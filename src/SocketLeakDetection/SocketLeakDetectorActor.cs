﻿// -----------------------------------------------------------------------
// <copyright file="SocketLeakDetectorActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2019 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Event;

namespace SocketLeakDetection
{
    /// <summary>
    ///     Simple data structure for self-contained EMWA mathematics.
    /// </summary>
    public struct EMWA
    {
        public EMWA(double alpha, double currentAvg)
        {
            Alpha = alpha;
            CurrentAvg = currentAvg;
        }

        public double Alpha { get; }

        public double CurrentAvg { get; }

        public EMWA Next(int nextValue)
        {
            return new EMWA(Alpha, Alpha * nextValue + (1 - Alpha) * CurrentAvg);
        }

        public static EMWA Init(int sampleSize, int firstReading)
        {
            var alpha = 2.0 / (sampleSize + 1);
            return new EMWA(alpha, firstReading);
        }

        public static double operator %(EMWA e1, EMWA e2)
        {
            return (e1.CurrentAvg - e2.CurrentAvg) / e1.CurrentAvg;
        }

        public static EMWA operator +(EMWA e1, int next)
        {
            return e1.Next(next);
        }
    }

    /// <summary>
    ///     The leak detection business logic.
    /// </summary>
    public sealed class LeakDetector
    {
        public const int DefaultShortSampleSize = 10;
        public const int DefaultLongSampleSize = 30;
        private bool _minThresholdBreached;

        public LeakDetector(SocketLeakDetectorSettings settings)
            : this(settings.MinPorts, settings.MaxDifference,
                settings.MaxPorts, settings.ShortSampleSize, settings.LongSampleSize)
        {
        }

        public LeakDetector(int minPortCount, double maxDifference, int maxPortCount,
            int shortSampleSize = DefaultShortSampleSize, int longSampleSize = DefaultLongSampleSize)
        {
            MinPortCount = minPortCount;
            if (MinPortCount < 1)
                throw new ArgumentOutOfRangeException(nameof(minPortCount),
                    "MinPortCount must be at least 1");

            MaxDifference = maxDifference;
            if (MaxDifference <= 0.0d)
                throw new ArgumentOutOfRangeException(nameof(maxDifference), "MaxDifference must be greater than 0.0");

            MaxPortCount = maxPortCount;
            if (MaxPortCount <= MinPortCount)
                throw new ArgumentOutOfRangeException(nameof(maxPortCount),
                    "MaxPortCount must be greater than MinPortCount");

            // default both EMWAs to the minimum port count.
            Short = EMWA.Init(shortSampleSize, minPortCount);
            Long = EMWA.Init(longSampleSize, minPortCount);
        }

        /// <summary>
        ///     Moving average - long
        /// </summary>
        public EMWA Long { get; private set; }

        /// <summary>
        ///     Moving average - short
        /// </summary>
        public EMWA Short { get; private set; }

        public double RelativeDifference => Short % Long;

        public double MaxDifference { get; }

        /// <summary>
        ///     Below this threshold, don't start tracking the rate of port growth.
        /// </summary>
        public int MinPortCount { get; }

        /// <summary>
        ///     If the port count exceeds this threshold, signal failure anyway regardless of the averages.
        ///     Meant to act as a stop-loss mechanism in the event of a _very_ slow upward creep in ports over time.
        /// </summary>
        public int MaxPortCount { get; }

        /// <summary>
        ///     The current number of ports.
        /// </summary>
        public int CurrentPortCount { get; private set; }

        /// <summary>
        ///     Returns <c>true</c> if the <see cref="CurrentPortCount" /> exceeds <see cref="MaxPortCount" />
        ///     or if <see cref="RelativeDifference" /> exceeds <see cref="MaxDifference" />.
        /// </summary>
        public bool ShouldFail => RelativeDifference >= MaxDifference || CurrentPortCount >= MaxPortCount;

        /// <summary>
        ///     Feed the next port count into the <see cref="LeakDetector" />.
        /// </summary>
        /// <param name="newPortCount">The updated port count.</param>
        /// <returns>The current <see cref="LeakDetector" /> instance but with updated state.</returns>
        public LeakDetector Next(int newPortCount)
        {
            CurrentPortCount = newPortCount;
            if (CurrentPortCount >= MinPortCount) // time to start using samples
            {
                // Used to signal that we've crossed the threshold
                if (!_minThresholdBreached)
                    _minThresholdBreached = true;

                Long += CurrentPortCount;
                Short += CurrentPortCount;
            }
            else if (_minThresholdBreached) // fell back below minimum for first time
            {
                _minThresholdBreached = false;

                // reset averages back to starting position
                Long = new EMWA(Long.Alpha, CurrentPortCount);
                Short = new EMWA(Short.Alpha, CurrentPortCount);
            }

            return this;
        }
    }

    public class SocketLeakDetectorActor : UntypedActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger();

        private readonly SocketLeakDetectorSettings _settings;

        /// <summary>
        ///     Fired when the system is in failure - cancelled when the numbers fall back in line.
        /// </summary>
        private ICancelable _breachSignal;

        private LeakDetector _leakDetector;
        private readonly IActorRef _supervisor;

        /// <summary>
        ///     Constructor will setup the values that we will need to determine if we need to message our supervisor actor in case
        ///     we experience an increase in TCP ports
        /// </summary>
        /// <param name="settings">The settings for this actor's leak detection algorithm.</param>
        /// <param name="supervisor">Actor Reference for the Supervisor Actor in charge of terminating Actor System</param>
        public SocketLeakDetectorActor(SocketLeakDetectorSettings settings, IActorRef supervisor)
        {
            _supervisor = supervisor;
            _settings = settings;
        }

        protected override void OnReceive(object message)
        {
            if (message is TcpCount count)
            {
                _leakDetector.Next(count.CurrentPortCount);

                //if (_log.IsDebugEnabled)
                //{
                    _log.Info("Received port count of {0} for interface {1}", count.CurrentPortCount,
                        count.HostInterface);
                    _log.Info("Danger threshold: {0} - Observed threshold: {1}", _leakDetector.MaxDifference, _leakDetector.RelativeDifference);
                //}
                    

                if (_leakDetector.ShouldFail && _breachSignal == null)
                {
                    _log.Warning(
                        "Current port count detected to be [{0}] for network [{1}] - triggering ActorSystem termination in {2} seconds unless port count stabilizes.",
                        count.CurrentPortCount, count.HostInterface, _settings.BreachDuration);
                    _breachSignal = Context.System.Scheduler.ScheduleTellOnceCancelable(_settings.BreachDuration, Self,
                        TimerExpired.Instance, ActorRefs.NoSender);
                }
                else if (!_leakDetector.ShouldFail && _breachSignal != null)
                {
                    _breachSignal?.Cancel();
                    _breachSignal = null;
                    _log.Warning(
                        "Port count back down to [{0}] for network [{1}] - within healthy levels. Cancelling shutdown.",
                        count.CurrentPortCount, count.HostInterface);
                }
            }
            else if (message is TimerExpired)
            {
                _supervisor.Tell(TcpPortUseSupervisor.Shutdown.Instance);
                _breachSignal = null;
            }
        }


        protected override void PreStart()
        {
            //A commonly used value for alpa is alpha = 2/(N+1). This is because the weights of an SMA and EMA have the same "center of mass"  when alpa(rma)=2/(N(rma)+1)
            //https://en.wikipedia.org/wiki/Moving_average#Exponential_moving_average
            _leakDetector = new LeakDetector(_settings);
        }

        protected override void PostStop()
        {
            _breachSignal?.Cancel();
        }

        public class TimerExpired
        {
            public static readonly TimerExpired Instance = new TimerExpired();

            private TimerExpired()
            {
            }
        }
    }
}