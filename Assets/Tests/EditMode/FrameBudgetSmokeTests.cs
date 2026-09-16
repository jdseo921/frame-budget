using NUnit.Framework;
using UnityEngine;

namespace FrameBudget.Tests
{
    /// <summary>One deliberately small test: it exists to prove the edit-mode test harness runs.</summary>
    public class FrameBudgetSmokeTests
    {
        [Test]
        public void NewSimConfig_IsTheNaiveBaseline()
        {
            SimConfig config = ScriptableObject.CreateInstance<SimConfig>();
            try
            {
                Assert.That(config.spatialHash, Is.False);
                Assert.That(config.zeroAlloc, Is.False);
                Assert.That(config.tickBudget, Is.False);
                Assert.That(config.gpuInstancing, Is.False);
                Assert.That(config.burstJobs, Is.False);
                Assert.That(config.TechniqueLabel, Is.EqualTo("baseline"));
                Assert.That(config.Validate(), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }
    }
}
