using Xunit;
using Zigote.Scripting;

namespace Zigote.Tests;

public class InstancingTests
{
    [Fact]
    public void Forwards_Submitted_Instances_To_The_Backend()
    {
        var fake = new FakeBackend();
        Instancing.Backend = fake;
        try
        {
            float[] mats = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1];
            Instancing.SetInstances(42u, mats, 1);

            Assert.Equal(1, fake.Calls);
            Assert.Equal(42u, fake.LastEntity);
            Assert.Equal(1, fake.LastCount);
            Assert.Equal(mats, fake.LastData);
        }
        finally
        {
            Instancing.Backend = null;
        }
    }

    [Fact]
    public void Clear_Submits_Zero_Count()
    {
        var fake = new FakeBackend();
        Instancing.Backend = fake;
        try
        {
            Instancing.Clear(7u);
            Assert.Equal(7u, fake.LastEntity);
            Assert.Equal(0, fake.LastCount);
        }
        finally
        {
            Instancing.Backend = null;
        }
    }

    [Fact]
    public void Is_A_Safe_NoOp_When_No_Backend()
    {
        Instancing.Backend = null;
        // Must not throw with no host backend (outside play mode).
        Instancing.SetInstances(1u, [1f, 2f, 3f], 0);
        Instancing.Clear(1u);
        Assert.False(Instancing.IsAvailable);
    }

    private sealed class FakeBackend : IInstancingBackend
    {
        public int Calls;
        public int LastCount = -1;
        public float[] LastData = [];
        public uint LastEntity;

        public string? LastName;

        public void SetInstances(uint entityId, ReadOnlySpan<float> matrices, int count)
        {
            Calls++;
            LastEntity = entityId;
            LastCount = count;
            LastData = matrices.ToArray();
        }

        public void SetInstances(string nodeName, ReadOnlySpan<float> matrices, int count)
        {
            Calls++;
            LastName = nodeName;
            LastCount = count;
            LastData = matrices.ToArray();
        }
    }
}
