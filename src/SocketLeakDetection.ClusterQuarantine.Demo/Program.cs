using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;

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

        static void Main(string[] args)
        {
            
        }
    }
}
