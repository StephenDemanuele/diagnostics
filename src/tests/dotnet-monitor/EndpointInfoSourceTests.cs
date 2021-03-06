﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Monitoring;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;
using Xunit.Abstractions;

namespace DotnetMonitor.UnitTests
{
    public class EndpointInfoSourceTests
    {
        private readonly ITestOutputHelper _outputHelper;

        public EndpointInfoSourceTests(ITestOutputHelper outputHelper)
        {
            _outputHelper = outputHelper;
        }

        /// <summary>
        /// Tests that the server endpoint info source has no connections
        /// if <see cref="ServerEndpointInfoSource.Listen"/> is not called.
        /// </summary>
        [Fact]
        public async Task ServerSourceNoListenTest()
        {
            await using var source = CreateServerSource(out string transportName);
            // Intentionally do not call Listen

            await using (var execution1 = StartTraceeProcess("LoggerRemoteTest", transportName))
            {
                execution1.Start();

                await Task.Delay(TimeSpan.FromSeconds(1));

                var endpointInfos = await GetEndpointInfoAsync(source);

                Assert.Empty(endpointInfos);

                _outputHelper.WriteLine("Stopping tracee.");
            }
        }

        /// <summary>
        /// Tests that the server endpoint info source has not connections if no processes connect to it.
        /// </summary>
        [Fact]
        public async Task ServerSourceNoConnectionsTest()
        {
            await using var source = CreateServerSource(out _);
            source.Listen();

            var endpointInfos = await GetEndpointInfoAsync(source);
            Assert.Empty(endpointInfos);
        }

        /// <summary>
        /// Tests that server endpoint info source should throw ObjectDisposedException
        /// from API surface after being disposed.
        /// </summary>
        [Fact]
        public async Task ServerSourceThrowsWhenDisposedTest()
        {
            var source = CreateServerSource(out _);
            source.Listen();

            await source.DisposeAsync();

            // Validate source surface throws after disposal
            Assert.Throws<ObjectDisposedException>(
                () => source.Listen());

            Assert.Throws<ObjectDisposedException>(
                () => source.Listen(1));

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => source.GetEndpointInfoAsync(CancellationToken.None));
        }

        /// <summary>
        /// Tests that server endpoint info source should throw an exception from
        /// <see cref="ServerEndpointInfoSource.Listen"/> and
        /// <see cref="ServerEndpointInfoSource.Listen(int)"/> after listening was already started.
        /// </summary>
        [Fact]
        public async Task ServerSourceThrowsWhenMultipleListenTest()
        {
            await using var source = CreateServerSource(out _);
            source.Listen();

            Assert.Throws<InvalidOperationException>(
                () => source.Listen());

            Assert.Throws<InvalidOperationException>(
                () => source.Listen(1));
        }

        /// <summary>
        /// Tests that the server endpoint info source can properly enumerate endpoint infos when a single
        /// target connects to it and "disconnects" from it.
        /// </summary>
        [Fact]
        public async Task ServerSourceAddRemoveSingleConnectionTest()
        {
            await using var source = CreateServerSource(out string transportName);
            source.Listen();

            var endpointInfos = await GetEndpointInfoAsync(source);
            Assert.Empty(endpointInfos);

            Task newEndpointInfoTask = source.WaitForNewEndpointInfoAsync(TimeSpan.FromSeconds(5));

            await using (var execution1 = StartTraceeProcess("LoggerRemoteTest", transportName))
            {
                await newEndpointInfoTask;

                execution1.Start();

                endpointInfos = await GetEndpointInfoAsync(source);

                var endpointInfo = Assert.Single(endpointInfos);
                VerifyConnection(execution1.TestRunner, endpointInfo);

                _outputHelper.WriteLine("Stopping tracee.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));

            endpointInfos = await GetEndpointInfoAsync(source);

            Assert.Empty(endpointInfos);
        }

        private TestServerEndpointInfoSource CreateServerSource(out string transportName)
        {
            transportName = ReversedServerHelper.CreateServerTransportName();
            _outputHelper.WriteLine("Starting server endpoint info source at '" + transportName + "'.");
            return new TestServerEndpointInfoSource(transportName, _outputHelper);
        }

        private RemoteTestExecution StartTraceeProcess(string loggerCategory, string transportName = null)
        {
            _outputHelper.WriteLine("Starting tracee.");
            string exePath = CommonHelper.GetTraceePath("EventPipeTracee", targetFramework: "net5.0");
            return RemoteTestExecution.StartProcess(exePath + " " + loggerCategory, _outputHelper, transportName);
        }

        private async Task<IEnumerable<IEndpointInfo>> GetEndpointInfoAsync(ServerEndpointInfoSource source)
        {
            _outputHelper.WriteLine("Getting endpoint infos.");
            using CancellationTokenSource cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await source.GetEndpointInfoAsync(cancellationSource.Token);
        }

        /// <summary>
        /// Verifies basic information on the connection and that it matches the target process from the runner.
        /// </summary>
        private static void VerifyConnection(TestRunner runner, IEndpointInfo endpointInfo)
        {
            Assert.NotNull(runner);
            Assert.NotNull(endpointInfo);
            Assert.Equal(runner.Pid, endpointInfo.ProcessId);
            Assert.NotEqual(Guid.Empty, endpointInfo.RuntimeInstanceCookie);
            Assert.NotNull(endpointInfo.Endpoint);
        }

        private sealed class TestServerEndpointInfoSource : ServerEndpointInfoSource
        {
            private readonly ITestOutputHelper _outputHelper;
            private readonly List<TaskCompletionSource<IpcEndpointInfo>> _addedEndpointInfoSources = new List<TaskCompletionSource<IpcEndpointInfo>>();

            public TestServerEndpointInfoSource(string transportPath, ITestOutputHelper outputHelper)
                : base(transportPath)
            {
                _outputHelper = outputHelper;
            }

            public async Task<IpcEndpointInfo> WaitForNewEndpointInfoAsync(TimeSpan timeout)
            {
                TaskCompletionSource<IpcEndpointInfo> addedEndpointInfoSource = new TaskCompletionSource<IpcEndpointInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timeoutCancellation = new CancellationTokenSource();
                var token = timeoutCancellation.Token;
                using var _ = token.Register(() => addedEndpointInfoSource.TrySetCanceled(token));

                lock (_addedEndpointInfoSources)
                {
                    _addedEndpointInfoSources.Add(addedEndpointInfoSource);
                }

                _outputHelper.WriteLine("Waiting for new endpoint info.");
                timeoutCancellation.CancelAfter(timeout);
                IpcEndpointInfo endpointInfo = await addedEndpointInfoSource.Task;
                _outputHelper.WriteLine("Notified of new endpoint info.");

                return endpointInfo;
            }

            internal override void OnAddedEndpointInfo(IpcEndpointInfo info)
            {
                _outputHelper.WriteLine($"Added endpoint info to collection: {info.ToTestString()}");
                
                lock (_addedEndpointInfoSources)
                {
                    foreach (var source in _addedEndpointInfoSources)
                    {
                        source.TrySetResult(info);
                    }
                    _addedEndpointInfoSources.Clear();
                }
            }

            internal override void OnRemovedEndpointInfo(IpcEndpointInfo info)
            {
                _outputHelper.WriteLine($"Removed endpoint info from collection: {info.ToTestString()}");
            }
        }
    }
}
