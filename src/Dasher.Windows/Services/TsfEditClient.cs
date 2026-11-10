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
    internal const int MaxTextLength = 512;
    private const uint ProtocolVersion = 1;
    private const int TimeoutMilliseconds = 1500;

    internal static uint ForegroundThread()
        => GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);

    internal static async Task<(int Result, ulong Epoch)> EditAsync(uint editorThread, ulong epoch, string value, bool delete)
    {
        if (editorThread == 0 || value.Length is 0 or > MaxTextLength)
            throw new ArgumentException("Invalid TSF edit.");

        using var deadline = new CancellationTokenSource(TimeoutMilliseconds);
        using var pipe = new NamedPipeServerStream($"Dasher.Tsf.Edit.v1.{editorThread}",
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var request = new byte[24 + MaxTextLength * sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(request, ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), (ushort)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), delete ? (ushort)1 : (ushort)0);
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
        var response = new byte[16];
        await pipe.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
        return (BinaryPrimitives.ReadInt32LittleEndian(response),
            BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8)));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
}
