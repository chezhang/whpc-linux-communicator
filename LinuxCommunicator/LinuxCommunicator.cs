﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Hpc.Activation;
using Microsoft.Hpc.Scheduler;
using Microsoft.Hpc.Scheduler.Communicator;
using System.Net;

namespace Microsoft.Hpc.Communicators.LinuxCommunicator
{
    public class LinuxCommunicator : IUnmanagedResourceCommunicator, IDisposable
    {
        private const string ResourceUriFormat = "http://{0}:50001/api/{1}/{2}";
        private const string CallbackUriHeaderName = "CallbackUri";
        private const string HpcFullKeyName = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\HPC";
        private const string ClusterNameKeyName = "ClusterName";

        private readonly string HeadNode = (string)Microsoft.Win32.Registry.GetValue(HpcFullKeyName, ClusterNameKeyName, null);
        private readonly int MonitoringPort = 9894;

        private HttpClient client;
        private WebServer server;
        private Monitoring.CounterDataSender sender;
        private Dictionary<string, Guid> nodeMap;

        private CancellationTokenSource cancellationTokenSource;

        private static LinuxCommunicator instance;

        public LinuxCommunicator()
        {
            if (instance != null)
            {
                throw new InvalidOperationException("An instance of LinuxCommunicator already exists.");
            }

            instance = this;
        }

        public void Dispose()
        {
            this.sender.CloseConnection();
            this.server.Dispose();
            this.client.Dispose();
            GC.SuppressFinalize(this);
        }

        public static LinuxCommunicator Instance
        {
            get { return instance; }
        }

        public string LinuxServiceHost
        {
            get;
            set;
        }

        public INodeCommTracing Tracer
        {
            get;
            private set;
        }

        public ISchedulerCallbacks Listener
        {
            get;
            private set;
        }

        public string NodeGroupName { get { return "LinuxNodes"; } }

        public void Accept(ISchedulerCallbacks listener) { this.Listener = listener; }

        public void SetTracer(INodeCommTracing tracer)
        {
            this.Tracer = tracer;
        }

        public bool Initialize()
        {
            this.Tracer.TraceInfo("Initializing LinuxCommunicator.");
            this.client = new HttpClient();
            this.server = new WebServer();

            if (this.HeadNode == null)
            {
                ArgumentNullException exp = new ArgumentNullException(ClusterNameKeyName);
                Tracer.TraceError("Failed to find registry value: {0}. {1}", ClusterNameKeyName, exp);
                throw exp;
            }

            this.sender = new Monitoring.CounterDataSender();
            this.sender.OpenConnection(this.HeadNode, this.MonitoringPort);

            IScheduler scheduler = new Scheduler.Scheduler();
            scheduler.Connect(this.HeadNode);
            this.nodeMap = new Dictionary<string, Guid>();
            foreach (ISchedulerNode node in scheduler.GetNodeList(null, null))
            {
                this.nodeMap.Add(node.Name.ToLower(), node.Guid);
            }
            scheduler.Close();

            this.Tracer.TraceInfo("Initialized LinuxCommunicator.");
            return true;
        }

        public bool Start()
        {
            this.Tracer.TraceInfo("Starting LinuxCommunicator.");
            this.cancellationTokenSource = new CancellationTokenSource();
            this.server.Start().Wait();

            return true;
        }

        public bool Stop()
        {
            this.Tracer.TraceInfo("Stopping LinuxCommunicator.");
            this.server.Stop();
            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;
            return true;
        }

        public void OnNodeStatusChange(string nodeName, bool reachable)
        {
        }

        public void EndJob(string nodeName, EndJobArg arg, NodeCommunicatorCallBack<EndJobArg> callback)
        {
            this.SendRequest("endjob", "taskcompleted", nodeName, callback, arg);
        }

        public void EndTask(string nodeName, EndTaskArg arg, NodeCommunicatorCallBack<EndTaskArg> callback)
        {
            this.SendRequest("endtask", "taskcompleted", nodeName, callback, arg);
        }

        public void StartJobAndTask(string nodeName, StartJobAndTaskArg arg, string userName, string password, ProcessStartInfo startInfo, NodeCommunicatorCallBack<StartJobAndTaskArg> callback)
        {
            this.SendRequest("startjobandtask", "taskcompleted", nodeName, callback, Tuple.Create(arg, startInfo));
        }

        public void StartJobAndTaskSoftCardCred(string nodeName, StartJobAndTaskArg arg, string userName, string password, byte[] certificate, ProcessStartInfo startInfo, NodeCommunicatorCallBack<StartJobAndTaskArg> callback)
        {
            this.SendRequest("startjobandtask", "taskcompleted", nodeName, callback, Tuple.Create(arg, startInfo));
        }

        public void StartTask(string nodeName, StartTaskArg arg, ProcessStartInfo startInfo, NodeCommunicatorCallBack<StartTaskArg> callback)
        {
            this.SendRequest("starttask", "taskcompleted", nodeName, callback, Tuple.Create(arg, startInfo));
        }

        public void Ping(string nodeName)
        {
            this.SendRequest<NodeCommunicatorCallBackArg>("ping", "computenodereported", nodeName, (name, arg, ex) =>
            {
                this.Tracer.TraceInfo("Compute node {0} pinged. Ex {1}", name, ex);
            }, null);
        }

        private void SendRequest<T>(string action, string callbackAction, string nodeName, NodeCommunicatorCallBack<T> callback, T arg) where T : NodeCommunicatorCallBackArg
        {
            var request = new HttpRequestMessage(HttpMethod.Post, this.GetResoureUri(nodeName, action));
            request.Headers.Add(CallbackUriHeaderName, this.GetCallbackUri(nodeName, callbackAction));
            var formatter = new JsonMediaTypeFormatter();
            request.Content = new ObjectContent<T>(arg, formatter);

            this.client.SendAsync(request, this.cancellationTokenSource.Token).ContinueWith(t =>
            {
                Exception ex = t.Exception;

                if (ex == null)
                {
                    try
                    {
                        t.Result.EnsureSuccessStatusCode();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                }

                callback(nodeName, arg, ex);
            }, this.cancellationTokenSource.Token);
        }

        private void SendRequest<T, T1>(string action, string callbackAction, string nodeName, NodeCommunicatorCallBack<T> callback, Tuple<T, T1> arg) where T : NodeCommunicatorCallBackArg
        {
            var request = new HttpRequestMessage(HttpMethod.Post, this.GetResoureUri(nodeName, action));
            request.Headers.Add(CallbackUriHeaderName, this.GetCallbackUri(nodeName, callbackAction));
            var formatter = new JsonMediaTypeFormatter();
            request.Content = new ObjectContent<Tuple<T, T1>>(arg, formatter);

            this.client.SendAsync(request, this.cancellationTokenSource.Token).ContinueWith(t =>
            {
                Exception ex = t.Exception;

                if (ex == null)
                {
                    try
                    {
                        t.Result.EnsureSuccessStatusCode();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                }

                callback(nodeName, arg.Item1, ex);
            }, this.cancellationTokenSource.Token);
        }

        private Uri GetResoureUri(string nodeName, string action)
        {
            var hostName = this.LinuxServiceHost ?? nodeName;
            return new Uri(string.Format(ResourceUriFormat, hostName, nodeName, action));
            //if (hostName.Contains("."))
            //{
            //    return new Uri(string.Format(ResourceUriFormat, hostName, nodeName, action));
            //}
            //else
            //{
            //    return new Uri(string.Format(ResourceUriWithDomainFormat, hostName, nodeName, action)); ;
            //}
        }

        private string GetCallbackUri(string nodeName, string action)
        {
      //      var callbackUri = "http://10.0.0.4:50000/api/{1}/{2}";

            return string.Format("{0}/api/{1}/{2}", this.server.ListeningUri, nodeName, action);
        }

        public void MetricReported(Monitoring.ComputeNodeMetricInformation MetricInfo)
        {
            Guid nodeid = GetNodeId(MetricInfo.Name);
            if (nodeid == Guid.Empty)
            {
                this.Tracer.TraceWarning("Ignore MetricData from UNKNOWN Node {0}.", MetricInfo.Name);
                return;
            }

            this.sender.SendData(nodeid, MetricInfo.GetUmids(), MetricInfo.GetValues(), MetricInfo.TickCount);

            this.Tracer.TraceInfo("Send MetricData from Node {0} ({1}).", MetricInfo.Name, nodeid);
        }

        private Guid GetNodeId(string nodeName)
        {
            Guid guid = Guid.Empty;
            if (string.IsNullOrEmpty(nodeName))
            {
                return guid;
            }
            
            string lower = nodeName.ToLower();
            if (this.nodeMap.ContainsKey(lower))
            {
                guid = this.nodeMap[lower];
            }
            else
            {
                // new node added ? refresh the nodemap
                IScheduler scheduler = new Scheduler.Scheduler();
                scheduler.Connect(this.HeadNode);
                foreach (ISchedulerNode node in scheduler.GetNodeList(null, null))
                {
                    string l = node.Name.ToLower();
                    if (!this.nodeMap.ContainsKey(l))
                    {
                        nodeMap.Add(l, node.Guid);
                    }
                    if (l.Equals(lower))
                    {
                        guid = node.Guid;
                    }
                }
                scheduler.Close();
            }

            return guid;
        }
    }
}
