using DetailFrame.App.Services;
using Xunit;

namespace DetailFrame.App.Tests;

public sealed class SavePlaceholderCleanerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("detailframe-placeholder-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Preserves_existing_empty_file_created_before_picker_opened()
    {
        var path = Path.Combine(_dir, "existing.png");
        File.WriteAllBytes(path, []);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        var pickerOpened = DateTime.UtcNow.AddMinutes(-1);

        Assert.False(SavePlaceholderCleaner.RemoveIfNewEmptyPlaceholder(path, pickerOpened));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Removes_empty_placeholder_created_after_picker_opened()
    {
        var pickerOpened = DateTime.UtcNow.AddSeconds(-5);
        var path = Path.Combine(_dir, "new.png");
        File.WriteAllBytes(path, []);

        Assert.True(SavePlaceholderCleaner.RemoveIfNewEmptyPlaceholder(path, pickerOpened));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Preserves_new_file_with_content()
    {
        var pickerOpened = DateTime.UtcNow.AddSeconds(-5);
        var path = Path.Combine(_dir, "data.png");
        File.WriteAllBytes(path, [1]);

        Assert.False(SavePlaceholderCleaner.RemoveIfNewEmptyPlaceholder(path, pickerOpened));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Missing_file_is_ignored()
    {
        Assert.False(SavePlaceholderCleaner.RemoveIfNewEmptyPlaceholder(Path.Combine(_dir, "none.png"), DateTime.UtcNow));
    }
}
