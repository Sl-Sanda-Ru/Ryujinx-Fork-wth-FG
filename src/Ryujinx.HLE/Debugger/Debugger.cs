using Gommon;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.Debugger.Gdb;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using IExecutionContext = Ryujinx.Cpu.IExecutionContext;

namespace Ryujinx.HLE.Debugger
{
    public partial class Debugger : IDisposable
    {
        internal Switch Device { get; private set; }

        public ushort GdbStubPort { get; private set; }

        private readonly BlockingCollection<Message.IMarker> _messages = new(1);
        private readonly Thread _debuggerThread;
        private readonly Thread _messageHandlerThread;

        private TcpListener _listenerSocket;
        private Socket _clientSocket;
        private NetworkStream _readStream;
        private NetworkStream _writeStream;

        private GdbCommands _commands;

        private bool _shuttingDown;
        private readonly ManualResetEventSlim _breakHandlerEvent = new(false);

        internal ulong? CThreadId;
        internal ulong? GThreadId;

        internal KThread CThread => CThreadId?.Into(DebugProcess.GetThread);

        internal KThread GThread => GThreadId?.Into(DebugProcess.GetThread);

        public readonly BreakpointManager BreakpointManager;

        public Debugger(Switch device, ushort port)
        {
            Device = device;
            GdbStubPort = port;

            ARMeilleure.Optimizations.EnableDebugging = true;

            _debuggerThread = new Thread(MainLoop);
            _debuggerThread.Start();
            _messageHandlerThread = new Thread(MessageHandlerMain);
            _messageHandlerThread.Start();
            BreakpointManager = new BreakpointManager(this);
        }

        internal KProcess Process => Device.System?.DebugGetApplicationProcess();
        internal IDebuggableProcess DebugProcess => Device.System?.DebugGetApplicationProcessDebugInterface();

        internal KThread[] GetThreads() => DebugProcess.ThreadUids.Select(DebugProcess.GetThread).ToArray();

        internal bool IsProcess32Bit => DebugProcess.GetThread(GThreadId ?? DebugProcess.ThreadUids.First()).Context.IsAarch32;

        internal bool WriteRegister(IExecutionContext ctx, int registerId, StringStream ss) =>
            IsProcess32Bit
                ? ctx.WriteRegister32(registerId, ss)
                : ctx.WriteRegister64(registerId, ss);

        internal string ReadRegister(IExecutionContext ctx, int registerId) =>
            IsProcess32Bit
                ? ctx.ReadRegister32(registerId)
                : ctx.ReadRegister64(registerId);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shuttingDown = true;

                _listenerSocket.Stop();
                _clientSocket?.Shutdown(SocketShutdown.Both);
                _clientSocket?.Close();
                _readStream?.Close();
                _writeStream?.Close();
                _debuggerThread.Join();
                _messages.Add(Message.Kill);
                _messageHandlerThread.Join();
                _messages.Dispose();
                _breakHandlerEvent.Dispose();
            }
        }

        private static readonly bool _watchNullX0 =
            System.Environment.GetEnvironmentVariable("RYUJINX_WATCH_NULLX0") == "1";

        private long _watchVus;

        private readonly ulong[] _dernieresCles = new ulong[32];
        private readonly ulong[] _dernieresAdr = new ulong[32];
        private int _curseurCles;

        public void VidangerCles()
        {
            if (!_watchNullX0)
            {
                return;
            }

            System.Text.StringBuilder sb = new();
            sb.AppendLine($"WATCH: {_watchVus} recherches vues. 32 dernieres cles (la plus recente en premier) :");

            for (int k = 0; k < _dernieresCles.Length; k++)
            {
                int i = ((_curseurCles - 1 - k) % _dernieresCles.Length + _dernieresCles.Length) % _dernieresCles.Length;

                if (_dernieresCles[i] == 0)
                {
                    continue;
                }

                sb.AppendLine($"   cle=0x{_dernieresCles[i]:X16}  appelee depuis 0x{_dernieresAdr[i]:X16}");
            }

            Logger.Error?.Print(LogClass.GdbStub, sb.ToString());
        }

        public void BreakHandler(IExecutionContext ctx, ulong address, int imm)
        {
            if (_watchNullX0)
            {
                ulong x0 = ctx.GetX(0);
                ulong x1 = ctx.GetX(1);

                _watchVus++;

                _dernieresCles[_curseurCles] = x1;
                _dernieresAdr[_curseurCles] = ctx.GetX(30);
                _curseurCles = (_curseurCles + 1) % _dernieresCles.Length;

                if (x0 != 0 || x1 == 0)
                {
                    return;
                }

                System.Text.StringBuilder sb = new();
                sb.AppendLine($"WATCH: APPEL FATAL a 0x{address:X16} apres {_watchVus} passages valides.");

                for (int r = 0; r <= 30; r++)
                {
                    sb.Append($" X{r}=0x{ctx.GetX(r):X16}");

                    if (r % 4 == 3)
                    {
                        sb.AppendLine();
                    }
                }

                Logger.Error?.Print(LogClass.GdbStub, sb.ToString());
                Logger.Error?.Print(LogClass.GdbStub,
                    $"WATCH: X30 (adresse de retour REELLE) = 0x{ctx.GetX(30):X16}");
                Logger.Flush();

                return;
            }

            DebugProcess.DebugInterruptHandler(ctx);

            _breakHandlerEvent.Reset();
            _messages.Add(new ThreadBreakMessage(ctx, address, imm));
            // Messages.Add can block, so we log it after adding the message to make sure user can see the log at the same time GDB receives the break message
            Logger.Notice.Print(LogClass.GdbStub, $"Break hit on thread {ctx.ThreadUid} at pc {address:x016}");
            // Wait for the process to stop before returning to avoid BreakHandler being called multiple times from the same breakpoint
            _breakHandlerEvent.Wait(5000);
        }

        public void StepHandler(IExecutionContext ctx)
        {
            DebugProcess.DebugInterruptHandler(ctx);
        }
    }
}
