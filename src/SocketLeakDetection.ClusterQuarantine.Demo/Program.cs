using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Akka.Remote;
using Akka.Routing;
using Akka.Util.Internal;

namespace SocketLeakDetection.ClusterQuarantine.Demo
{
    public class SilenceActor : ReceiveActor
    {
        public SilenceActor()
        {
            ReceiveAny(o =>
            {

            });
        }
    }

    class Program
    {
        private static Config ClusterConfig(int portNumber)
        {
            return @"
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.port = """+ portNumber + @"""
                akka.remote.dot-netty.tcp.hostname = 127.0.0.1
                akka.actor.deployment{
                    /random {
			            router = random-group
			            routees.paths = [""/user/silence""]
			            cluster{
				            enabled = on
				            allow-local-routees = off
			            }
		            }
                }
                akka.cluster.seed-nodes = [""akka.tcp://quarantine-test@127.0.0.1:9444""]
            ";
        }

        public static ActorSystem StartNode(int portNumber)
        {
            var node = ActorSystem.Create("quarantine-test", ClusterConfig(portNumber));
            var silence = node.ActorOf(Props.Create(() => new SilenceActor()), "silence");
            var router = node.ActorOf(Props.Empty.WithRouter(FromConfig.Instance), "random");
            node.Scheduler.ScheduleTellRepeatedly(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), router, "hit", ActorRefs.NoSender);

            return node;
        }

        static async Task Main(string[] args)
        {
            // launch seed node
            var seed = StartNode(9444);
            var leakDetector = seed.ActorOf(Props.Create(() => new TcpPortUseSupervisor(new[]{ IPAddress.Loopback })), "portMonitor");

            // start node that will be quarantined
            var quarantineNode = StartNode(9555);
            var node2Addr = Cluster.Get(quarantineNode).SelfAddress;
            var uid = AddressUidExtension.Uid(quarantineNode);

            //var peanutGallery = Enumerable.Repeat(1, 3).Select(x => StartNode(0)).ToList();

            Func<int, bool> checkMembers = i => Cluster.Get(seed).State.Members.Count == i;

            seed.Log.Info("Waiting for members to join...");
            while (!checkMembers(2))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            seed.Log.Info("Cluster up.");

            RarpFor(seed).Quarantine(node2Addr, uid);

            seed.WhenTerminated.Wait();
        }

        static RemoteActorRefProvider RarpFor(ActorSystem system)
        {
            return system.AsInstanceOf<ExtendedActorSystem>().Provider.AsInstanceOf<RemoteActorRefProvider>();
        }
    }
}
