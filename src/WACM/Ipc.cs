using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WACM;
public static class Ipc
{
    public const string PipeName = "WACM.Local.v1";
    public static PipeSecurity Security(string owner)
    {
        var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }) acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner), PipeAccessRights.ReadWrite, AccessControlType.Allow)); return acl;
    }
    public static async Task Serve(string owner, Func<string, Task<string>> handler, AppLog log, CancellationToken token, string pipeName = PipeName)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security(owner));
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await Read(pipe, timeout.Token); var answer = await handler(request); await Write(pipe, answer, timeout.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log.Write("WARN", "NamedPipe", "IPC", ex.Message); }
        }
    }
    public static async Task<string> Call(string request, string pipeName = PipeName, TimeSpan? timeout = null)
    {
        using var token = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync((int)Math.Clamp((timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds, 1, int.MaxValue), token.Token); await Write(pipe, request, token.Token); return await Read(pipe, token.Token);
    }
    public static async Task Write(Stream stream, string text, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(text); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("IPC上限超過");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<string> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); int length = BitConverter.ToInt32(header);
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("IPCメッセージ長が不正です。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token); return Encoding.UTF8.GetString(bytes);
    }
}
