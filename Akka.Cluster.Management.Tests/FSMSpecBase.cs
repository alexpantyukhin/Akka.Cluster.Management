﻿using System;
using System.Collections.Generic;
using Akka.Actor;
using Akka.Actor.Internal;
using Akka.TestKit;

namespace Akka.Cluster.Management.Tests
{
    public abstract class FSMSpecBase<State, Data> : TestKit.Xunit2.TestKit
    {
        private TimeSpan TransitionTimeout;

        private FSMSpecBase(ActorSystem system) : base(system)
        {
            // TODO: Config settings
            Settings = new ClusterDiscoverySettings
            {

            };
            StateProbe = CreateTestProbe(system);
            TransitionTimeout = TimeSpan.FromSeconds(10);
        }

        protected FSMSpecBase() : this(new ActorSystemImpl("testsystem"))
        {
        }

        public TestProbe StateProbe { get; set; }
        public ClusterDiscoverySettings Settings { get; set; }

        public void ExpectTransitionTo(State expState)
        {
            var val = StateProbe.ExpectMsg<FSMBase.Transition<State>>(TransitionTimeout);
            Assertions.AssertTrue(EqualityComparer<State>.Default.Equals(val.To, expState));
        }

        public void ExpectInitialState(State expState)
        {
            var val = StateProbe.ExpectMsg<FSMBase.CurrentState<State>>(TransitionTimeout);
            Assertions.AssertTrue(EqualityComparer<State>.Default.Equals(val.State, expState));
        }

        protected override void AfterAll()
        {
            Shutdown(Sys);
        }
    }
}
