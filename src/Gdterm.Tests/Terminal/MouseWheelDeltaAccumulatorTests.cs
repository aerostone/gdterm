using System;
using Gdterm.Terminal;

namespace Gdterm.Tests.Terminal
{
    public static class MouseWheelDeltaAccumulatorTests
    {
        public static void Run()
        {
            var acc = new MouseWheelDeltaAccumulator();
            Assert.Equal(0, acc.Add(30), "partial touchpad delta waits for a full detent");
            Assert.Equal(1, acc.Add(90), "partial deltas combine into one detent");
            Assert.Equal(0, acc.Add(-60), "opposite direction cancels the retained fraction");
            Assert.Equal(-1, acc.Add(-60), "negative full detent is emitted");
            Assert.Equal(2, acc.Add(240), "multiple detents are preserved");
        }
    }
}
