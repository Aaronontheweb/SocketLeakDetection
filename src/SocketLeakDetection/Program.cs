﻿
using Akka.Actor;
using Akka.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SocketLeakDetection
{
    class Program
    {
        static void Main(string[] args)
        {
            var Sys = ActorSystem.Create("Test");
            var Config = ConfigurationFactory.ParseString(File.ReadAllText("akka.hocon"));
            var watcher = Sys.ActorOf(Props.Create(() => new Watcher()));
            var sup = Sys.ActorOf(Props.Create(() => new Supervisor(Sys, Config)));

            Sys.WhenTerminated.Wait();
        }
    }
}
