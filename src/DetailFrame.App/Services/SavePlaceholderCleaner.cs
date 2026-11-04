namespace DetailFrame.App.Services;

// The system save picker pre-creates an empty file for a new name. After a failed save that placeholder
// should go, but an existing empty file the user chose to overwrite must never be deleted. A file is only
// treated as a placeholder when it is empty AND was created after the picker was opened; anything
// uncertain (unreadable timestamps, errors) is left alone.
public static class SavePlaceholderCleaner
{
    public static bool RemoveIfNewEmptyPlaceholder(string path, DateTime pickerOpenedUtc)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != 0 || info.CreationTimeUtc < pickerOpenedUtc)
            {
                return false;
            }

            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
