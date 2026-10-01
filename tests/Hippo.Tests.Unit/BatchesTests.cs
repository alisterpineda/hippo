using Hippo.Indexing;

namespace Hippo.Tests.Unit;

public class BatchesTests
{
    [Fact]
    public void Items_are_written_a_full_batch_at_a_time_and_the_rest_on_flush()
    {
        var written = new List<List<int>>();
        var batches = new Batches<int>(200, batch => written.Add([.. batch]));

        foreach (var item in Enumerable.Range(0, 450))
        {
            batches.Add(item);
        }
        Assert.Equal([200, 200], written.Select(b => b.Count));

        batches.Flush();
        Assert.Equal([200, 200, 50], written.Select(b => b.Count));
        Assert.Equal(Enumerable.Range(0, 450), written.SelectMany(b => b));
    }

    [Fact]
    public void No_more_than_one_batch_is_ever_held()
    {
        var held = 0;
        var batches = new Batches<int>(3, batch => held = Math.Max(held, batch.Count));
        var most = 0;

        foreach (var item in Enumerable.Range(0, 10))
        {
            batches.Add(item);
            most = Math.Max(most, batches.Pending);
        }
        batches.Flush();

        Assert.Equal((3, 2, 0), (held, most, batches.Pending));
    }

    [Fact]
    public void Flushing_with_nothing_pending_writes_nothing()
    {
        var writes = 0;
        var batches = new Batches<int>(3, _ => writes++);

        batches.Flush();

        Assert.Equal(0, writes);
    }
}
