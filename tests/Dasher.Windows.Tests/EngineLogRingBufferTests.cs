using Dasher.Windows.Services;

namespace Dasher.Windows.Tests;

public class EngineLogRingBufferTests
{
    [Fact]
    public void Empty_buffer_snapshots_to_empty_string()
    {
        var buf = new EngineLogRingBuffer();
        Assert.Equal("", buf.Snapshot());
    }

    [Fact]
    public void Append_adds_line_with_level_prefix()
    {
        var buf = new EngineLogRingBuffer();
        buf.Append(1, "Training loaded");
        Assert.Contains("[I] Training loaded", buf.Snapshot());
    }

    [Theory]
    [InlineData(0, "[D]")]
    [InlineData(1, "[I]")]
    [InlineData(2, "[W]")]
    [InlineData(3, "[E]")]
    [InlineData(9, "[X]")]
    public void Append_uses_correct_level_prefix(int level, string expectedPrefix)
    {
        var buf = new EngineLogRingBuffer();
        buf.Append(level, "msg");
        Assert.Contains(expectedPrefix, buf.Snapshot());
    }

    [Fact]
    public void Buffer_respects_max_lines()
    {
        var buf = new EngineLogRingBuffer(maxLines: 5);
        for (int i = 0; i < 10; i++)
            buf.Append(1, $"line {i}");

        Assert.Equal(5, buf.Count);
        var snapshot = buf.Snapshot();
        Assert.Contains("line 5", snapshot);
        Assert.Contains("line 9", snapshot);
        Assert.DoesNotContain("line 0", snapshot);
        Assert.DoesNotContain("line 4", snapshot);
    }

    [Fact]
    public void Buffer_respects_max_bytes()
    {
        var buf = new EngineLogRingBuffer(maxLines: 100, maxBytes: 50);
        // Each line is ~20 bytes ("[I] " + 15 chars)
        for (int i = 0; i < 10; i++)
            buf.Append(1, new string('x', 15));

        // Should have evicted oldest to stay under 50 bytes
        var snapshot = buf.Snapshot();
        var totalBytes = snapshot.Length;
        Assert.True(totalBytes <= 100, $"Snapshot too large: {totalBytes}");
        Assert.True(buf.Count < 10, "Should have evicted some lines");
    }

    [Fact]
    public void Buffer_preserves_order()
    {
        var buf = new EngineLogRingBuffer();
        buf.Append(1, "first");
        buf.Append(2, "second");
        buf.Append(3, "third");

        var snapshot = buf.Snapshot();
        var lines = snapshot.Split('\n');
        Assert.Equal("first", lines[0].Replace("[I] ", ""));
        Assert.Equal("second", lines[1].Replace("[W] ", ""));
        Assert.Equal("third", lines[2].Replace("[E] ", ""));
    }

    [Fact]
    public void Clear_empties_buffer()
    {
        var buf = new EngineLogRingBuffer();
        buf.Append(1, "test");
        buf.Clear();
        Assert.Equal(0, buf.Count);
        Assert.Equal("", buf.Snapshot());
    }
}
