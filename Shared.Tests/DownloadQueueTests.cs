using ProsperoThemes.App.Library;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>The queue of store downloads asked for while the app was busy (ThemeApp/Library/DownloadQueue.cs).</summary>
public class DownloadQueueTests
{
    private sealed record Item(string Id, string Name);

    private static DownloadQueue<Item> New() => new(item => item.Id);

    private static string[] Drain(DownloadQueue<Item> queue)
    {
        var ids = new List<string>();
        while (queue.TryDequeue(out Item item, out _)) { ids.Add(item.Id); queue.Finish(); }
        return [.. ids];
    }

    [Fact]
    public void Downloads_come_out_in_the_order_they_went_in()
    {
        DownloadQueue<Item> queue = New();
        Assert.True(queue.Enqueue(new Item("a", "A")));
        Assert.True(queue.Enqueue(new Item("b", "B")));
        Assert.True(queue.Enqueue(new Item("c", "C")));
        Assert.Equal(3, queue.Count);
        Assert.Equal(["a", "b", "c"], Drain(queue));
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.PendingIds);
    }

    [Fact]
    public void An_empty_queue_has_nothing_to_dequeue()
    {
        DownloadQueue<Item> queue = New();
        Assert.False(queue.TryDequeue(out Item? item, out bool then));
        Assert.Null(item);
        Assert.False(then);
        Assert.Null(queue.ActiveId);
    }

    [Fact]
    public void A_theme_is_not_queued_twice_nor_while_it_downloads()
    {
        DownloadQueue<Item> queue = New();
        Assert.True(queue.Enqueue(new Item("a", "A")));
        Assert.False(queue.Enqueue(new Item("a", "A again")));
        Assert.Equal(1, queue.Count);

        queue.TryDequeue(out _, out _);
        Assert.Equal("a", queue.ActiveId);
        Assert.False(queue.Enqueue(new Item("a", "A")), "the download on its way counts as queued");
        Assert.True(queue.Contains("a"));
        Assert.False(queue.IsPending("a"));

        queue.Finish();
        Assert.Null(queue.ActiveId);
        Assert.False(queue.Contains("a"));
        Assert.True(queue.Enqueue(new Item("a", "A")), "an update can be asked for again once the download is over");
    }

    [Fact]
    public void The_flag_for_what_follows_comes_back_with_its_download()
    {
        DownloadQueue<Item> queue = New();
        queue.Enqueue(new Item("a", "A"), then: true);
        queue.Enqueue(new Item("b", "B"));
        Assert.True(queue.TryDequeue(out Item first, out bool firstThen));
        Assert.Equal("a", first.Id);
        Assert.True(firstThen);
        Assert.True(queue.TryDequeue(out Item second, out bool secondThen));
        Assert.Equal("b", second.Id);
        Assert.False(secondThen);
    }

    [Fact]
    public void The_pending_ids_follow_the_queue_and_leave_the_one_downloading_out()
    {
        DownloadQueue<Item> queue = New();
        IReadOnlySet<string> ids = queue.PendingIds;
        queue.Enqueue(new Item("a", "A"));
        queue.Enqueue(new Item("b", "B"));
        Assert.Equal(["a", "b"], ids.OrderBy(i => i));   // the same set, seen through the reference taken before
        queue.TryDequeue(out _, out _);
        Assert.Equal(["b"], ids);
        Assert.Equal("a", queue.ActiveId);
        queue.Clear();
        Assert.Empty(ids);
        Assert.Same(ids, queue.PendingIds);
    }

    [Fact]
    public void Begin_marks_a_download_that_did_not_wait_in_the_queue()
    {
        DownloadQueue<Item> queue = New();
        queue.Begin("x");
        Assert.True(queue.Contains("x"));
        Assert.False(queue.Enqueue(new Item("x", "X")));
        queue.Finish();
        Assert.False(queue.Contains("x"));
    }

    [Fact]
    public void Clear_drops_what_waits_but_not_the_download_on_its_way()
    {
        DownloadQueue<Item> queue = New();
        queue.Enqueue(new Item("a", "A"));
        queue.Enqueue(new Item("b", "B"));
        queue.TryDequeue(out _, out _);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Equal("a", queue.ActiveId);
        Assert.False(queue.TryDequeue(out _, out _));
    }

    [Fact]
    public void A_dequeue_without_a_finish_does_not_hold_the_queue()
    {
        DownloadQueue<Item> queue = New();
        queue.Enqueue(new Item("a", "A"));
        queue.Enqueue(new Item("b", "B"));
        queue.TryDequeue(out _, out _);
        queue.TryDequeue(out _, out _);   // Finish was never called for "a"
        Assert.Equal("b", queue.ActiveId);
        Assert.False(queue.Contains("a"));
    }
}
