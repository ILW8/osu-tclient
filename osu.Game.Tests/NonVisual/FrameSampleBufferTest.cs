// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using osu.Game.Utils.FramePacing;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class FrameSampleBufferTest
    {
        private static FrameSample sample(long index, double delta) =>
            new FrameSample(index, index * 1000, delta, 0, 0, 0, 0);

        [Test]
        public void TestRejectsNonPowerOfTwoCapacity()
        {
            Assert.Throws<ArgumentException>(() => new FrameSampleBuffer(3));
        }

        [Test]
        public void TestRejectsZeroCapacity()
        {
            Assert.Throws<ArgumentException>(() => new FrameSampleBuffer(0));
        }

        [Test]
        public void TestEmptyBuffer()
        {
            var buffer = new FrameSampleBuffer(4);

            ClassicAssert.AreEqual(0, buffer.TotalWritten);
            ClassicAssert.IsFalse(buffer.HasWrapped);
            ClassicAssert.AreEqual(0, buffer.Snapshot().Length);
        }

        [Test]
        public void TestPartialFillPreservesOrder()
        {
            var buffer = new FrameSampleBuffer(4);

            buffer.Add(sample(0, 1.0));
            buffer.Add(sample(1, 2.0));

            var snapshot = buffer.Snapshot();

            ClassicAssert.AreEqual(2, snapshot.Length);
            ClassicAssert.AreEqual(0, snapshot[0].FrameIndex);
            ClassicAssert.AreEqual(1, snapshot[1].FrameIndex);
            ClassicAssert.AreEqual(2.0, snapshot[1].DeltaMilliseconds);
            ClassicAssert.IsFalse(buffer.HasWrapped);
        }

        [Test]
        public void TestExactFillDoesNotReportWrapped()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 4; i++)
                buffer.Add(sample(i, i));

            ClassicAssert.AreEqual(4, buffer.Snapshot().Length);
            ClassicAssert.IsFalse(buffer.HasWrapped);
        }

        [Test]
        public void TestOverflowKeepsMostRecentInOrder()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 6; i++)
                buffer.Add(sample(i, i));

            var snapshot = buffer.Snapshot();

            ClassicAssert.AreEqual(4, snapshot.Length);
            ClassicAssert.AreEqual(2, snapshot[0].FrameIndex);
            ClassicAssert.AreEqual(5, snapshot[3].FrameIndex);
            ClassicAssert.IsTrue(buffer.HasWrapped);
            ClassicAssert.AreEqual(6, buffer.TotalWritten);
        }

        [Test]
        public void TestClearResetsState()
        {
            var buffer = new FrameSampleBuffer(4);

            for (int i = 0; i < 6; i++)
                buffer.Add(sample(i, i));

            buffer.Clear();

            ClassicAssert.AreEqual(0, buffer.TotalWritten);
            ClassicAssert.IsFalse(buffer.HasWrapped);
            ClassicAssert.AreEqual(0, buffer.Snapshot().Length);
        }
    }
}
