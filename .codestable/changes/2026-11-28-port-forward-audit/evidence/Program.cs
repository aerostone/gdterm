using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using Gdterm.Tunnel;
using Gdterm.Core.Models;
using Renci.SshNet;
class Program {
 static int fails, observations;
 static void Check(bool ok,string detail){Console.WriteLine((ok?"CONFIRMED ":"UNCONFIRMED ")+detail);observations++;if(!ok)fails++;}
 static SshClient Connect(int port,string key){var c=new SshClient("127.0.0.1",port,Environment.UserName,new PrivateKeyFile(key));c.Connect();return c;}
 static PortForwardRule Rule(string id,int local,int remote){return new PortForwardRule{Id=id,LocalPort=local,RemotePort=remote};}
 static void Main(string[] args){int port=int.Parse(args[0]);
 using(var a=Connect(port,args[1])) using(var b=Connect(port,args[1])) {
  using(var manager=new PortForwardManager()){
   var rule=Rule("drop",PortForwardManager.FindAvailablePort(21000),port);
   Check(manager.StartLocal(a,rule),"baseline local forwarding starts");
   using(var socket=new TcpClient("127.0.0.1",rule.LocalPort)){socket.ReceiveTimeout=3000;var data=new byte[128];int n=socket.GetStream().Read(data,0,data.Length);Check(System.Text.Encoding.ASCII.GetString(data,0,n).StartsWith("SSH-"),"baseline forwarding carries SSH banner");}
   var original=a.ForwardedPorts.Single();a.Disconnect();Thread.Sleep(150);
   Check(manager.IsActive(rule.Id)&&!original.IsStarted,"disconnect stops port but manager continues reporting active");
   Check(!manager.StartLocal(b,rule),"stale rule blocks restart on replacement connected SSH client");
   Check(manager.Stop(a,rule.Id)&&!manager.IsActive(rule.Id),"explicit stop clears disconnected record (excluded candidate)");
  }
  a.Connect();
  using(var manager=new PortForwardManager()){
   var occupied=new TcpListener(IPAddress.Loopback,0);occupied.Start();
   var rule=Rule("busy",((IPEndPoint)occupied.LocalEndpoint).Port,port);
   int before=a.ForwardedPorts.Count();
   for(int i=0;i<3;i++)Check(!manager.StartLocal(a,rule),"occupied local port start rejected "+i);
   Check(a.ForwardedPorts.Count()==before+3,"failed start retains one port in SSH client per attempt");
   occupied.Stop();
  }
  using(var c=Connect(port,args[1])){
   var manager=new PortForwardManager();var rule=Rule("dispose",PortForwardManager.FindAvailablePort(22000),port);
   manager.StartLocal(c,rule);manager.Dispose();
   Check(c.ForwardedPorts.Count()==1,"manager dispose leaves forwarded port registered in live SSH client");
  }
  using(var c=Connect(port,args[1]))using(var manager=new PortForwardManager()){
   int pid=int.Parse(c.RunCommand("echo $PPID").Result.Trim());
   var stop=System.Diagnostics.Process.Start("kill","-STOP "+pid);stop.WaitForExit();
   Check(stop.ExitCode==0,"pause only isolated SSH session process");
   var release=new Thread(()=>{Thread.Sleep(1000);var resume=System.Diagnostics.Process.Start("kill","-CONT "+pid);resume.WaitForExit();});release.Start();
   var watch=System.Diagnostics.Stopwatch.StartNew();
   bool started=manager.StartRemote(c,Rule("wait",port,PortForwardManager.FindAvailablePort(23000)));
   watch.Stop();release.Join();
   Check(started&&watch.ElapsedMilliseconds>=800,"remote start synchronously blocks caller while server delayed: "+watch.ElapsedMilliseconds+"ms");
  }
  using(var c=Connect(port,args[1]))using(var manager=new PortForwardManager()){
   var rule=Rule("zero",0,port);
   Check(manager.StartLocal(c,rule)&&rule.LocalPort==0&&((ForwardedPortLocal)c.ForwardedPorts.Single()).BoundPort>0,"zero port starts at random actual port but rule remains port zero");
  }
 }
 Console.WriteLine("observations="+observations+" unmatched="+fails);Environment.ExitCode=fails==0?0:1;
 }
}