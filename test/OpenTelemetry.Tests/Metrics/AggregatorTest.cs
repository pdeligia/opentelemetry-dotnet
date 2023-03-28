// <copyright file="AggregatorTest.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Xunit;

namespace OpenTelemetry.Metrics.Tests
{
    public class AggregatorTest
    {
        private readonly AggregatorStore aggregatorStore = new("test", AggregationType.Histogram, AggregationTemporality.Cumulative, 1024, Metric.DefaultHistogramBounds);

        [Fact]
        public void HistogramDistributeToAllBucketsDefault()
        {
            var histogramPoint = new MetricPoint(this.aggregatorStore, AggregationType.Histogram, null, null, Metric.DefaultHistogramBounds);
            histogramPoint.Update(-1);
            histogramPoint.Update(0);
            histogramPoint.Update(2);
            histogramPoint.Update(5);
            histogramPoint.Update(8);
            histogramPoint.Update(10);
            histogramPoint.Update(11);
            histogramPoint.Update(25);
            histogramPoint.Update(40);
            histogramPoint.Update(50);
            histogramPoint.Update(70);
            histogramPoint.Update(75);
            histogramPoint.Update(99);
            histogramPoint.Update(100);
            histogramPoint.Update(246);
            histogramPoint.Update(250);
            histogramPoint.Update(499);
            histogramPoint.Update(500);
            histogramPoint.Update(999);
            histogramPoint.Update(1000);
            histogramPoint.Update(1001);
            histogramPoint.Update(10000000);
            histogramPoint.TakeSnapshot(true);

            var count = histogramPoint.GetHistogramCount();

            Assert.Equal(22, count);

            int actualCount = 0;
            foreach (var histogramMeasurement in histogramPoint.GetHistogramBuckets())
            {
                Assert.Equal(2, histogramMeasurement.BucketCount);
                actualCount++;
            }
        }

        [Fact]
        public void HistogramDistributeToAllBucketsCustom()
        {
            var boundaries = new double[] { 10, 20 };
            var histogramPoint = new MetricPoint(this.aggregatorStore, AggregationType.Histogram, null, null, boundaries);

            // 5 recordings <=10
            histogramPoint.Update(-10);
            histogramPoint.Update(0);
            histogramPoint.Update(1);
            histogramPoint.Update(9);
            histogramPoint.Update(10);

            // 2 recordings >10, <=20
            histogramPoint.Update(11);
            histogramPoint.Update(19);

            histogramPoint.TakeSnapshot(true);

            var count = histogramPoint.GetHistogramCount();
            var sum = histogramPoint.GetHistogramSum();

            // Sum of all recordings
            Assert.Equal(40, sum);

            // Count  = # of recordings
            Assert.Equal(7, count);

            int index = 0;
            int actualCount = 0;
            var expectedBucketCounts = new long[] { 5, 2, 0 };
            foreach (var histogramMeasurement in histogramPoint.GetHistogramBuckets())
            {
                Assert.Equal(expectedBucketCounts[index], histogramMeasurement.BucketCount);
                index++;
                actualCount++;
            }

            Assert.Equal(boundaries.Length + 1, actualCount);
        }

        [Fact]
        public void HistogramWithOnlySumCount()
        {
            var boundaries = Array.Empty<double>();
            var histogramPoint = new MetricPoint(this.aggregatorStore, AggregationType.HistogramSumCount, null, null, boundaries);

            histogramPoint.Update(-10);
            histogramPoint.Update(0);
            histogramPoint.Update(1);
            histogramPoint.Update(9);
            histogramPoint.Update(10);
            histogramPoint.Update(11);
            histogramPoint.Update(19);

            histogramPoint.TakeSnapshot(true);

            var count = histogramPoint.GetHistogramCount();
            var sum = histogramPoint.GetHistogramSum();

            // Sum of all recordings
            Assert.Equal(40, sum);

            // Count  = # of recordings
            Assert.Equal(7, count);

            // There should be no enumeration of BucketCounts and ExplicitBounds for HistogramSumCount
            var enumerator = histogramPoint.GetHistogramBuckets().GetEnumerator();
            Assert.False(enumerator.MoveNext());
        }

        [Fact]
        public void MultithreadedLongHistogramTest_Coyote()
        {
            var config = Configuration.Create();
            var test = TestingEngine.Create(config, this.MultiThreadedHistogramUpdateAndSnapShotTest);

            test.Run();
            Console.WriteLine(test.GetReport());
            Console.WriteLine($"Bugs, if any: {string.Join("\n", test.TestReport.BugReports)}");

            var dir = Directory.GetCurrentDirectory();

            if (test.TryEmitReports(dir, "MultithreadedLongHistogramTest_Coyote", out IEnumerable<string> reportPaths))
            {
                foreach (var reportPath in reportPaths)
                {
                    Console.WriteLine($"Execution Report: {reportPath}");
                }
            }

            if (test.TryEmitCoverageReports(dir, "MultithreadedLongHistogramTest_Coyote", out reportPaths))
            {
                foreach (var reportPath in reportPaths)
                {
                    Console.WriteLine($"Coverage report: {reportPath}");
                }
            }

            Assert.Equal(0, test.TestReport.NumOfFoundBugs);
        }

        [Fact]
        public void MultiThreadedHistogramUpdateAndSnapShotTest()
        {
            var boundaries = Array.Empty<double>();
            var histogramPoint = new MetricPoint(this.aggregatorStore, AggregationType.HistogramSumCount, null, null, boundaries);
            var argsToThread = new ThreadArguments
            {
                HistogramPoint = histogramPoint,
                MreToEnsureAllThreadsStart = new ManualResetEvent(false),
            };

            var numberOfThreads = 10;
            var snapshotThread = new Thread(HistogramSnapshotThread);
            Thread[] updateThreads = new Thread[numberOfThreads];
            for (int i = 0; i < numberOfThreads; ++i)
            {
                updateThreads[i] = new Thread(HistogramUpdateThread);
                updateThreads[i].Start(argsToThread);
            }

            argsToThread.MreToEnsureAllThreadsStart.WaitOne();
            snapshotThread.Start(argsToThread);

            for (int i = 0; i < numberOfThreads; ++i)
            {
                updateThreads[i].Join();
            }

            snapshotThread.Join();

            var sum = histogramPoint.GetHistogramSum();
            Assert.Equal(400, sum);
        }

        private static void HistogramSnapshotThread(object obj)
        {
            if (obj is not ThreadArguments args)
            {
                throw new Exception("invalid args");
            }

            var mreToEnsureAllThreadsStart = args.MreToEnsureAllThreadsStart;
            mreToEnsureAllThreadsStart.WaitOne();

            while (Interlocked.Read(ref args.ThreadsFinishedAllUpdatesCount) != 10)
            {
                args.HistogramPoint.TakeSnapshot(outputDelta: false);
            }

            // ensure the last snapshot will be called
            Thread.Sleep(1000);
            for (int i = 0; i < 10; ++i)
            {
                args.HistogramPoint.TakeSnapshot(outputDelta: false);
            }
        }

        private static void HistogramUpdateThread(object obj)
        {
            if (obj is not ThreadArguments args)
            {
                throw new Exception("invalid args");
            }

            var mreToEnsureAllThreadsStart = args.MreToEnsureAllThreadsStart;

            if (Interlocked.Increment(ref args.ThreadStartedCount) == 10)
            {
                mreToEnsureAllThreadsStart.Set();
            }

            args.HistogramPoint.Update(-10);
            args.HistogramPoint.Update(0);
            args.HistogramPoint.Update(1);
            args.HistogramPoint.Update(9);

            Thread.Sleep(1000);

            args.HistogramPoint.Update(10);
            args.HistogramPoint.Update(11);
            args.HistogramPoint.Update(19);

            Interlocked.Increment(ref args.ThreadsFinishedAllUpdatesCount);
        }

        private class ThreadArguments
        {
            public MetricPoint HistogramPoint;
            public ManualResetEvent MreToEnsureAllThreadsStart;
            public int ThreadStartedCount;
            public long ThreadsFinishedAllUpdatesCount;
        }
    }
}
