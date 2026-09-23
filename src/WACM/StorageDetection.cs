namespace WACM;
public static class StorageDetection
{
    public static (string Kind, int Parallel) Detect(string path)
    {
        try
        {
            var volume = Native.Volume(path).Guid.TrimEnd('\'); using var h = Native.Open(volume);
            var query = new byte[12]; var data = new byte[4096];
            if (!Native.DeviceIoControl(h, 0x2D1400, query, 12, data, data.Length, out var n, IntPtr.Zero) || n < 32) return ("不明", 1);
            var nvme = BitConverter.ToUInt32(data, 28) == 17;
            BitConverter.GetBytes(7).CopyTo(query, 0);
            if (!Native.DeviceIoControl(h, 0x2D1400, query, 12, data, data.Length, out n, IntPtr.Zero) || n < 9) return ("不明", 1);
            if (data[8] != 0) return ("HDD", 1);
            return nvme ? ("NVMe", 4) : ("SSD", 2);
        }
        catch { return ("不明（媒体判定失敗）", 1); }
    }
}
