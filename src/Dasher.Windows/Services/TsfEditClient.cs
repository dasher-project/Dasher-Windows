using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dasher.Windows.Services;

internal static class TsfEditClient
{
    internal enum EditOperation : ushort { Insert = 0, Delete = 1, Backspace = 3, Left = 4, Right = 5, Copy = 6, Cut = 7, Paste = 8 }
    internal const int MaxTextLength = 512;
    private const uint ProtocolVersion = 3;
    private const int TimeoutMilliseconds = 1500;

    internal static uint ForegroundEditorProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == (uint)Environment.ProcessId ? 0 : process;
    }

    internal static async Task<(int Result, ulong Epoch)> EditAsync(uint editorProcess, ulong epoch, string value, EditOperation operation)
    {
        if (value.Length > MaxTextLength || (value.Length == 0 && operation is EditOperation.Insert or EditOperation.Delete)) throw new ArgumentException("Invalid TSF edit.");
        var response = await RequestAsync(editorProcess, epoch, value, (ushort)operation)
            .ConfigureAwait(false);
        return (BinaryPrimitives.ReadInt32LittleEndian(response),
            BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8)));
    }

    internal static async Task<(string Before, ulong Epoch)?> ReadAsync(uint editorProcess)
    {
        var response = await RequestAsync(editorProcess, 0, "", 2).ConfigureAwait(false);
        if (BinaryPrimitives.ReadInt32LittleEndian(response) != 0) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(4));
        if (length is < 0 or > MaxTextLength) throw new IOException("Invalid TSF response length.");
        return (Encoding.Unicode.GetString(response.AsSpan(16, length * sizeof(char))),
            BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8)));
    }

    private static async Task<byte[]> RequestAsync(uint editorProcess, ulong epoch, string value, ushort operation)
    {
        if (editorProcess == 0) throw new ArgumentException("No focused editor.");

        using var deadline = new CancellationTokenSource(TimeoutMilliseconds);
        using var pipe = new NamedPipeServerStream($"Dasher.Tsf.Edit.v3.{editorProcess}",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var request = new byte[24 + MaxTextLength * sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(request, ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), (ushort)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), operation);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8),
            (ulong)(Environment.TickCount64 + TimeoutMilliseconds));
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), epoch);
        Encoding.Unicode.GetBytes(value, request.AsSpan(24));

        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("No TSF service connected for the focused editor.");
        }
        await pipe.WriteAsync(request, deadline.Token).ConfigureAwait(false);
        var response = new byte[operation == 2 ? 16 + MaxTextLength * sizeof(char) : 16];
        await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
        return response;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
