﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.Management.SeedList;
using Akka.Cluster.Management.ServiceDiscovery;
using Akka.TestKit;
using Moq;
using Xunit;

namespace Akka.Cluster.Management.Tests
{
    public class SeedListActorSpec : FSMSpecBase<SeedListState, ISeedListData>
    {
        private const string Addr1 = "akka.tcp://system@host1:50000";
        private const string Addr2 = "akka.tcp://system@host2:50000";

        private IActorRef Init()
        {
            return Init(Settings);
        }

        private IActorRef Init(ClusterDiscoverySettings clusterDiscoverySettings)
        {
            var seedList = Sys.ActorOf(Props.Create(() => new SeedListActor(ServiceDiscoveryClientMock.Object, clusterDiscoverySettings)));
            seedList.Tell(new FSMBase.SubscribeTransitionCallBack(StateProbe.Ref));
            ExpectInitialState(SeedListState.AwaitingInitialState);
            return seedList;
        }

        //private Task<GetNodesResponse> FetchSeedsReq()
        //{
        //    return ServiceDiscoveryClientMock.Get(Settings.SeedsPath, true, true);
        //}

        [Fact]
        private void Seed_list_manager_actor_should_proceed_to_AwaitCommand_when_seed_lists_are_empty()
        {
            var seedsTask = new TaskCompletionSource<GetNodesResponse>();
            ServiceDiscoveryClientMock.Setup(s => s.Get(Settings.SeedsPath)).Returns(seedsTask.Task);

            var seedList = Init();

            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }

        [Fact]
        private void It_should_delete_stale_seeds()
        {
            var seedsTask = new TaskCompletionSource<GetNodesResponse>();
            var deleteTask1 = new TaskCompletionSource<DeleteNodeResponse>();
            var deleteTask2 = new TaskCompletionSource<DeleteNodeResponse>();
            ServiceDiscoveryClientMock.Setup(s => s.Get(Settings.SeedsPath)).Returns(seedsTask.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Delete($"{Settings.SeedsPath}/{131}", null, It.IsAny<bool>())).Returns(deleteTask1.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Delete($"{Settings.SeedsPath}/{132}", null, It.IsAny<bool>())).Returns(deleteTask2.Task);

            var seedList = Init();

            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()
            {
                {$"{Settings.SeedsPath}/{131}", Addr1 },
                {$"{Settings.SeedsPath}/{132}", Addr2 }
            }));

            ExpectTransitionTo(SeedListState.AwaitingCommand);
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            deleteTask1.SetResult(new DeleteNodeResponse(Addr1));
            deleteTask2.SetResult(new DeleteNodeResponse(Addr2));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }

        [Fact]
        // TODO: fix this test.
        private void it_should_register_initial_seeds()
        {
            var seedsTask = new TaskCompletionSource<GetNodesResponse>();
            var createTask1 = new TaskCompletionSource<CreateNodeResponse>();
            var createTask2 = new TaskCompletionSource<CreateNodeResponse>();
            ServiceDiscoveryClientMock.Setup(s => s.Get(Settings.SeedsPath)).Returns(seedsTask.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Create(Settings.SeedsPath, Addr1, It.IsAny<TimeSpan>())).Returns(createTask1.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Create(Settings.SeedsPath, Addr2, It.IsAny<TimeSpan>())).Returns(createTask2.Task);

            var seedList = Init();

            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty.Add(Addr1).Add(Addr2)));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            createTask1.SetResult(new CreateNodeResponse(Addr1));
            createTask2.SetResult(new CreateNodeResponse(Addr2));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }

        [Fact]
        private void it_should_handle_MemberAdded_MemberRemoved_commands()
        {
            var seedsTask = new TaskCompletionSource<GetNodesResponse>();
            var createTask1 = new TaskCompletionSource<CreateNodeResponse>();
            var deleteTask1 = new TaskCompletionSource<DeleteNodeResponse>();
            ServiceDiscoveryClientMock.Setup(s => s.Get(Settings.SeedsPath)).Returns(seedsTask.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Create(Settings.SeedsPath, Addr1, It.IsAny<TimeSpan?>())).Returns(createTask1.Task);
            ServiceDiscoveryClientMock.Setup(s => s.Delete(Settings.SeedsPath, "131", It.IsAny<bool>())).Returns(deleteTask1.Task);

            var seedList = Init();
            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()));
            ExpectTransitionTo(SeedListState.AwaitingCommand);

            seedList.Tell(new MemberAdded(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            createTask1.SetResult(new CreateNodeResponse(Addr1) {Key = "131"});
            ExpectTransitionTo(SeedListState.AwaitingCommand);

            seedList.Tell(new MemberRemoved(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            deleteTask1.SetResult(new DeleteNodeResponse(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }

        [Fact]
        private void it_should_retry_fetching_registered_seeds_in_case_of_errors()
        {
            var seedsErrorTask = new TaskCompletionSource<GetNodesResponse>();
            var seedsSuccessTask = new TaskCompletionSource<GetNodesResponse>();

            ServiceDiscoveryClientMock.SetupSequence(s => s.Get(Settings.SeedsPath))
                .Returns(seedsErrorTask.Task)
                .Returns(seedsSuccessTask.Task);

            var seedList = Init(Settings);
            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsErrorTask.SetException(new Exception());
            ExpectTransitionTo(SeedListState.AwaitingInitialState);
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsSuccessTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()));
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }

        private void it_should_retry_Add_Remove_operations_in_case_of_errors()
        {
            var seedsTask = new TaskCompletionSource<GetNodesResponse>();
            var createTaskError = new TaskCompletionSource<CreateNodeResponse>();
            var createTask1 = new TaskCompletionSource<CreateNodeResponse>();
            var deleteTaskError = new TaskCompletionSource<Response>();
            var deleteTask1 = new TaskCompletionSource<Response>();
            //  when(fetchSeedsReq).thenReturn(seedsTask.future)
            //  when(createReq1).thenReturn(createTaskError.future).thenReturn(createTask1.future)
            //  when(deleteReq1).thenReturn(deleteTaskError.future).thenReturn(deleteTask1.future)

            var seedList = Init(Settings);
            seedList.Tell(new InitialState(ImmutableHashSet<string>.Empty));
            ExpectTransitionTo(SeedListState.AwaitingRegisteredSeeds);

            seedsTask.SetResult(new GetNodesResponse(new Dictionary<string, string>()));
            ExpectTransitionTo(SeedListState.AwaitingCommand);

            seedList.Tell(new MemberAdded(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            //createTaskError.failure(failure);
            ExpectTransitionTo(SeedListState.AwaitingCommand);
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            createTask1.SetResult(new CreateNodeResponse(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingCommand);

            seedList.Tell(new MemberRemoved(Addr1));
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            //deleteTaskError.failure(failure);
            ExpectTransitionTo(SeedListState.AwaitingCommand);
            ExpectTransitionTo(SeedListState.AwaitingEtcdReply);

            deleteTask1.SetResult(new Response());
            ExpectTransitionTo(SeedListState.AwaitingCommand);
        }
    }
}
