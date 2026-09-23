namespace WACM;
public static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    public static string Human(decimal bytes)
    {
        var magnitude = Math.Abs(bytes);
        decimal divisor = 1;
        int unit = 0;
        while (magnitude >= 1024 && unit < Units.Length - 1)
        {
            magnitude /= 1024;
            divisor *= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{bytes / divisor:N2} {Units[unit]}";
    }
}